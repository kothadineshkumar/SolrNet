﻿#region license
// Copyright (c) 2007-2009 Mauricio Scheffer
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//      http://www.apache.org/licenses/LICENSE-2.0
//  
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
#endregion

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Xml;
using SolrNet.Exceptions;
using SolrNet.Utils;

namespace SolrNet.Impl {
    /// <summary>
    /// Default query results parser.
    /// Parses xml query results
    /// </summary>
    /// <typeparam name="T">Document type</typeparam>
    public class SolrQueryResultParser<T> : ISolrQueryResultParser<T> where T : new() {

        private readonly IReadOnlyMappingManager mappingManager;

        private static readonly IDictionary<string, Type> solrTypes;

        static SolrQueryResultParser() {
            solrTypes = new Dictionary<string, Type>();
            solrTypes["int"] = typeof (int);
            solrTypes["str"] = typeof (string);
            solrTypes["bool"] = typeof (bool);
            solrTypes["date"] = typeof (DateTime);
        }

        public SolrQueryResultParser(IReadOnlyMappingManager mapper) {
            mappingManager = mapper;
        }

        /// <summary>
        /// Parses solr's xml response
        /// </summary>
        /// <param name="r">solr xml response</param>
        /// <returns>query results</returns>
        public ISolrQueryResults<T> Parse(string r) {
            var results = new SolrQueryResults<T>();
            var xml = new XmlDocument();
            xml.LoadXml(r);
            var resultNode = xml.SelectSingleNode("response/result");
            results.NumFound = Convert.ToInt32(resultNode.Attributes["numFound"].InnerText);
            var maxScore = resultNode.Attributes["maxScore"];
            if (maxScore != null) {
                results.MaxScore = double.Parse(maxScore.InnerText, CultureInfo.InvariantCulture.NumberFormat);
            }
            var allFields = mappingManager.GetFields(typeof (T));
            foreach (XmlNode docNode in xml.SelectNodes("response/result/doc")) {
                results.Add(ParseDocument(docNode, allFields));
            }
            var mainFacetNode = xml.SelectSingleNode("response/lst[@name='facet_counts']");
            if (mainFacetNode != null) {
                results.FacetQueries = ParseFacetQueries(mainFacetNode);
                results.FacetFields = ParseFacetFields(mainFacetNode);
            }

            var responseHeaderNode = xml.SelectSingleNode("response/lst[@name='responseHeader']");
            if (responseHeaderNode != null) {
                results.Header = ParseHeader(responseHeaderNode);
            }

            var highlightingNode = xml.SelectSingleNode("response/lst[@name='highlighting']");
            if (highlightingNode != null)
                results.Highlights = ParseHighlighting(results, highlightingNode);

            return results;
        }

        public IDictionary<string, ICollection<KeyValuePair<string, int>>> ParseFacetFields(XmlNode node) {
            var d = new Dictionary<string, ICollection<KeyValuePair<string, int>>>();
            foreach (XmlNode fieldNode in node.SelectSingleNode("lst[@name='facet_fields']").ChildNodes) {
                var field = fieldNode.Attributes["name"].Value;
                var c = new List<KeyValuePair<string, int>>();
                foreach (XmlNode facetNode in fieldNode.ChildNodes) {
                    var key = facetNode.Attributes["name"].Value;
                    var value = Convert.ToInt32(facetNode.InnerText);
                    c.Add(new KeyValuePair<string, int>(key, value));
                }
                d[field] = c;
            }
            return d;
        }

        public IDictionary<string, int> ParseFacetQueries(XmlNode node) {
            var d = new Dictionary<string, int>();
            foreach (XmlNode fieldNode in node.SelectSingleNode("lst[@name='facet_queries']").ChildNodes) {
                var key = fieldNode.Attributes["name"].Value;
                var value = Convert.ToInt32(fieldNode.InnerText);
                d[key] = value;
            }
            return d;
        }

        private delegate bool BoolFunc(PropertyInfo[] p);

        public DateTime ParseDate(string s) {
            return DateTime.ParseExact(s, "yyyy-MM-dd'T'HH:mm:ss.FFF'Z'", CultureInfo.InvariantCulture);
        }

        public void SetProperty(T doc, PropertyInfo prop, XmlNode field) {
            // HACK too messy
            if (field.Name == "arr") {
                prop.SetValue(doc, GetCollectionProperty(field, prop), null);
            } else if (prop.PropertyType == typeof (double?)) {
                if (!string.IsNullOrEmpty(field.InnerText))
                    prop.SetValue(doc, double.Parse(field.InnerText, CultureInfo.InvariantCulture), null);
            } else if (prop.PropertyType == typeof (DateTime)) {
                prop.SetValue(doc, ParseDate(field.InnerText), null);
            } else if (prop.PropertyType == typeof (DateTime?)) {
                if (!string.IsNullOrEmpty(field.InnerText))
                    prop.SetValue(doc, ParseDate(field.InnerText), null);
            } else {
                var converter = TypeDescriptor.GetConverter(prop.PropertyType);
                if (converter.CanConvertFrom(typeof (string)))
                    prop.SetValue(doc, converter.ConvertFromInvariantString(field.InnerText), null);
                else
                    prop.SetValue(doc, Convert.ChangeType(field.InnerText, prop.PropertyType), null);
            }
        }

        private static object GetCollectionProperty(XmlNode field, PropertyInfo prop) {
            try {
                var genericTypes = prop.PropertyType.GetGenericArguments();
                if (genericTypes.Length == 1) {
                    // ICollection<int>, etc
                    return GetGenericCollectionProperty(field, genericTypes);
                }
                if (prop.PropertyType.IsArray) {
                    // int[], string[], etc
                    return GetArrayProperty(field, prop);
                }
                if (prop.PropertyType.IsInterface) {
                    // ICollection
                    return GetNonGenericCollectionProperty(field);
                }
            } catch (Exception e) {
                throw new CollectionTypeNotSupportedException(e, prop.PropertyType);
            }
            throw new CollectionTypeNotSupportedException(prop.PropertyType);
        }

        private static IList GetNonGenericCollectionProperty(XmlNode field) {
            var l = new ArrayList();
            foreach (XmlNode arrayValueNode in field.ChildNodes) {
                l.Add(Convert.ChangeType(arrayValueNode.InnerText, solrTypes[arrayValueNode.Name]));
            }
            return l;
        }

        private static Array GetArrayProperty(XmlNode field, PropertyInfo prop) {
            // int[], string[], etc
            var arr = (Array) Activator.CreateInstance(prop.PropertyType, new object[] {field.ChildNodes.Count});
            var arrType = Type.GetType(prop.PropertyType.ToString().Replace("[]", ""));
            int i = 0;
            foreach (XmlNode arrayValueNode in field.ChildNodes) {
                arr.SetValue(Convert.ChangeType(arrayValueNode.InnerText, arrType), i);
                i++;
            }
            return arr;
        }

        private static IList GetGenericCollectionProperty(XmlNode field, Type[] genericTypes) {
            // ICollection<int>, etc
            var gt = genericTypes[0];
            var l = (IList) Activator.CreateInstance(typeof (List<>).MakeGenericType(gt));
            foreach (XmlNode arrayValueNode in field.ChildNodes) {
                l.Add(Convert.ChangeType(arrayValueNode.InnerText, gt));
            }
            return l;
        }

        /// <summary>
        /// Builds a document from the corresponding response xml node
        /// </summary>
        /// <param name="node">response xml node</param>
        /// <param name="fields">document fields</param>
        /// <returns>populated document</returns>
        public T ParseDocument(XmlNode node, ICollection<KeyValuePair<PropertyInfo, string>> fields) {
            var doc = new T();
            foreach (XmlNode field in node.ChildNodes) {
                string fieldName = field.Attributes["name"].InnerText;
                var property = Func.FirstOrDefault(fields, kv => kv.Value == fieldName);
                if (property.Key == null)
                    continue;
                try {
                    SetProperty(doc, property.Key, field);
                } catch (Exception e) {
                    throw new SolrNetException(string.Format("Error setting property {0} from field {1}", property.Key.Name, fieldName), e);
                }
            }
            return doc;
        }

        public ResponseHeader ParseHeader(XmlNode node) {
            var r = new ResponseHeader();
            r.Status = int.Parse(node.SelectSingleNode("int[@name='status']").InnerText);
            r.QTime = int.Parse(node.SelectSingleNode("int[@name='QTime']").InnerText);
            r.Params = new Dictionary<string, string>();
            var paramNodes = node.SelectNodes("lst[@name='params']/str");
            if (paramNodes != null) {
                foreach (XmlNode n in paramNodes) {
                    r.Params[n.Attributes["name"].InnerText] = n.InnerText;
                }				
            }
            return r;
        }

        public IDictionary<string, T> IndexResultsByKey(IEnumerable<T> results) {
            var r = new Dictionary<string, T>();
            var prop = mappingManager.GetUniqueKey(typeof (T)).Key;
            foreach (var d in results) {
                var key = prop.GetValue(d, null).ToString();
                r[key] = d;
            }
            return r;
        }

        public IDictionary<string, string> ParseHighlightingFields(XmlNodeList nodes) {
            var fields = new Dictionary<string, string>();
            foreach (XmlNode field in nodes) {
                var fieldName = field.Attributes["name"].InnerText;
                fields[fieldName] = field.InnerText;
            }
            return fields;
        }

        public IDictionary<T, IDictionary<string, string>> ParseHighlighting(IEnumerable<T> results, XmlNode node) {
            var r = new Dictionary<T, IDictionary<string, string>>();
            var docRefs = node.SelectNodes("lst");
            if (docRefs == null)
                return r;
            var resultsByKey = IndexResultsByKey(results);
            foreach (XmlNode docRef in docRefs) {
                var docRefKey = docRef.Attributes["name"].InnerText;
                var doc = resultsByKey[docRefKey];
                r[doc] = ParseHighlightingFields(docRef.ChildNodes);
            }
            return r;
        }
    }
}