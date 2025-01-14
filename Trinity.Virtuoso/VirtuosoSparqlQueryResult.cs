﻿// LICENSE:
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
// AUTHORS:
//
//  Moritz Eberl <moritz@semiodesk.com>
//  Sebastian Faubel <sebastian@semiodesk.com>
//
// Copyright (c) Semiodesk GmbH 2018

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Data;
using OpenLink.Data.Virtuoso;
using System.Diagnostics;

namespace Semiodesk.Trinity.Store.Virtuoso
{
    /// <summary>
    /// The results returned from a SPARQL query.
    /// </summary>
    internal class VirtuosoSparqlQueryResult : ISparqlQueryResult
    {
        #region Members

        private readonly VirtuosoStore _store;

        private readonly IModel _model;

        private readonly ISparqlQuery _query;

        private readonly ITransaction _transaction;

        private bool _isOrdered => !string.IsNullOrEmpty(_query.GetRootOrderByClause());

        #endregion

        #region Constructors

        /// <summary>
        /// Internal constructor which parses the results returned from a given query.
        /// </summary>
        /// <param name="query">The executed query.</param>
        /// <param name="store"></param>
        /// <param name="transaction"></param>
        /// <param name="model"></param>
        internal VirtuosoSparqlQueryResult(IModel model, ISparqlQuery query, VirtuosoStore store, ITransaction transaction=null)
        {
            _store = store;
            _transaction = transaction;
            _query = query;
            _model = model;
        }

        #endregion

        #region Methods

        /// <summary>
        /// Takes a data value from the _queryResults datatable and returns a marshalled data object.
        /// </summary>
        /// <param name="cellValue">A cell value from a Virtuoso results datatable.</param>
        /// <returns>A reference to the marshalled data object.</returns>
        private object ParseCellValue(object cellValue)
        {
            if (cellValue is SqlExtendedString extendedString)
            {
                var isBlankId = extendedString.IsBlankId();

                if (extendedString.IsResource() || isBlankId)
                {
                    // NOTE: We create an UriRef for correct equality comparision with fragment identifiers.
                    return new UriRef(extendedString.ToString(), isBlankId);
                }
                else if (extendedString.IsString())
                {
                    return extendedString.ToString();
                }
            }
            else if (cellValue is SqlRdfBox box)
            {
                if (box.StrType != null)
                {
                    try
                    {
                        // NOTE: We create an UriRef for correct equality comparision with fragment identifiers.
                        return XsdTypeMapper.DeserializeString(box.Value.ToString(), new UriRef(box.StrType));
                    }
                    catch (KeyNotFoundException)
                    {
                        // The given data type is not known by the XsdTypeMapper.
                        return box.Value.ToString();
                    }
                }
                else if ( box.StrLang != null)
                {
                    return new Tuple<string, string>(box.Value.ToString(), box.StrLang);
                }
                else if(box.Value != null)
                {
                    return box.Value.ToString();
                }
            }
            else if (cellValue is int)
            {
                // TODO: We need a different approach to store and read booleans.
                return cellValue;
            }
            else if (cellValue is VirtuosoDateTime dt)
            {
                // Virtuoso delivers the time not as UTC but as "unspecified"
                // we convert it to local time
                return  dt.Value.ToUniversalTime();
            }
            else if (cellValue is VirtuosoDateTimeOffset dto)
            {
                return dto.Value.UtcDateTime.ToUniversalTime();
            }
            
            return cellValue;
        }

        /// <summary>
        /// Generates BindingSet object from the data in _queryResults.
        /// </summary>
        /// <returns>An enumeration of BindingSet objects.</returns>
        private IEnumerable<BindingSet> GenerateBindings(DataTable queryResults)
        {
            foreach (DataRow row in queryResults.Rows)
            {
                var binding = new BindingSet();

                foreach (DataColumn column in queryResults.Columns)
                {
                    binding[column.Caption] = ParseCellValue(row[column]);
                }

                yield return binding;
            }
        }

        /// <summary>
        /// Tries to marshall the data in the _queryResults variable as Resource objects.
        /// </summary>
        /// <typeparam name="T">The Resource type.</typeparam>
        /// <returns>An enumeration of marshalled objects of the given type.</returns>
        private IEnumerable<Resource> GenerateResources(Type type, DataTable queryResults)
        {
            var result = new List<Resource>();

            if (0 < queryResults.Columns.Count)
            {
                // A list of global scope variables without the ?. Used to access the
                // subject, predicate and object variable in statement providing queries.
                var vars = _query.GetGlobalScopeVariableNames();

                var providesStatements = _query.ProvidesStatements();

                // A dictionary mapping URIs to the generated resource objects.
                var cache = new Dictionary<string, Resource>();

                var types = FindResourceTypes(type,
                    queryResults,
                    queryResults.Columns[0].ColumnName,
                    queryResults.Columns[1].ColumnName,
                    queryResults.Columns[2].ColumnName,
                    _query.IsInferenceEnabled);

                foreach (var resourceType in types)
                {
                    cache.Add(resourceType.Key, resourceType.Value);
                }

                // A handle to the currently built resource which may spare the lookup in the dictionary.
                Resource currentResource = null;

                foreach (DataRow row in queryResults.Rows)
                {
                    var subjectBlank = false;
                    var objectBlank = false;

                    if (row[0] is SqlExtendedString x)
                    {
                        subjectBlank = x.IsBlankId();
                    }

                    if (row[2] is SqlExtendedString y)
                    {
                        objectBlank = y.IsBlankId();
                    }

                    // Note: We create an UriRef for correct equality comparision with fragment identifiers.
                    UriRef s, predUri;
                    Property p;
                    object o;

                    if (_query.QueryType == SparqlQueryType.Describe ||
                        _query.QueryType == SparqlQueryType.Construct)
                    {
                        s = new UriRef(row[0].ToString(), subjectBlank);
                        predUri = new UriRef(row[1].ToString());
                        o = ParseCellValue(row[2]);
                    }
                    else if (_query.QueryType == SparqlQueryType.Select && providesStatements)
                    {
                        s = new UriRef(row[vars[0]].ToString(), subjectBlank);
                        predUri = new UriRef(row[vars[1]].ToString());
                        o = ParseCellValue(row[vars[2]]);
                    }
                    else
                    {
                        break;
                    }

                    p = OntologyDiscovery.GetProperty(predUri);

                    if (currentResource != null && currentResource.Uri.OriginalString == s.OriginalString)
                    {
                        // We already have the handle to the resource which the property should be added to.
                    }
                    else if (cache.ContainsKey(s.OriginalString))
                    {
                        currentResource = cache[s.OriginalString];

                        // In this case we may have encountered a resource which was 
                        // added to the cache by the object value handler below.
                        if (!result.Contains(currentResource))
                        {
                            result.Add(currentResource);
                        }
                    }
                    else
                    {
                        try
                        {
                            currentResource = (Resource) Activator.CreateInstance(type, s);
                            currentResource.IsNew = false;
                            currentResource.IsSynchronized = true;
                            currentResource.SetModel(_model);

                            cache.Add(s.OriginalString, currentResource);

                            result.Add(currentResource);
                        }
                        catch
                        {
#if DEBUG
                            Debug.WriteLine("[SparqlQueryResult] Info: Could not create resource " + s.OriginalString);
#endif

                            continue;
                        }
                    }

                    if (currentResource == null)
                    {
                        continue;
                    }

                    if (o is UriRef)
                    {
                        var uri = o as UriRef;

                        if (cache.ContainsKey(uri.OriginalString))
                        {
                            currentResource.AddPropertyToMapping(p, cache[uri.OriginalString], true);
                            currentResource.IsNew = false;
                            currentResource.IsSynchronized = false;
                            currentResource.SetModel(_model);
                        }
                        else
                        {
                            var r = new Resource(uri)
                            {
                                IsNew = false
                            };

                            cache.Add(uri.OriginalString, r);

                            currentResource.AddPropertyToMapping(p, r, true);
                            currentResource.IsNew = false;
                            currentResource.IsSynchronized = false;
                            currentResource.SetModel(_model);
                        }
                    }
                    else
                    {
                        currentResource.AddPropertyToMapping(p, o, true);
                    }
                }
            }

            foreach (var r in result)
            {
                yield return r;
            }
        }

        /// <summary>
        /// This method gets the RDF classes from the query result 
        /// and tries to match it to a C# class.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="subjectColumn"></param>
        /// <param name="preducateColumn"></param>
        /// <param name="objectColumn"></param>
        /// <param name="queryResults"></param>
        /// <param name="inferencingEnabled"></param>
        /// <returns></returns>
        private Dictionary<string, Resource> FindResourceTypes(Type type, DataTable queryResults, string subjectColumn, string preducateColumn, string objectColumn, bool inferencingEnabled = false)
        {
            var result = new Dictionary<string, Resource>();
            var types = new Dictionary<string, List<Class>>();
            string s, p, o;

            // Collect all types for every resource in the types dictionary.
            // I was going to use _queryResults.Select(), but that doesn't work with Virtuoso.
            foreach (DataRow row in queryResults.Rows)
            {
                s = row[subjectColumn].ToString();
                p = row[preducateColumn].ToString();

                if (p == "http://www.w3.org/1999/02/22-rdf-syntax-ns#type")
                {
                    o = row[objectColumn].ToString();

                    if (!types.ContainsKey(s))
                    {
                        types.Add(s, new List<Class>());
                    }

                    types[s].Add(OntologyDiscovery.Classes.ContainsKey(o)
                        ? OntologyDiscovery.Classes[o]
                        : new Class(new Uri(o)));
                }
            }

            // Iterate over all types and find the right class and instantiate it.
            foreach (var subject in types.Keys)
            {
                IList<Type> classType = MappingDiscovery.GetMatchingTypes(types[subject], type, inferencingEnabled);

                if (classType.Count > 0)
                {
                    #if DEBUG
                    if (classType.Count > 1)
                    {
                        const string msg = "Info: There is more that one assignable type for <{0}>. It was initialized using the first.";
                        Debug.WriteLine(string.Format(msg, subject));
                    }
                    #endif

                    var resource = (Resource)Activator.CreateInstance(classType[0], new UriRef(subject));
                    resource.SetModel(_model);
                    resource.IsNew = false;

                    result[subject] = resource;
                }
                #if DEBUG
                else if (type != typeof(Resource))
                {
                    var msg = "Info: No assignable type found for <{0}>.";

                    if (inferencingEnabled)
                    {
                        msg += " Try disabling inference.";
                    }

                    Debug.WriteLine(string.Format(msg, subject));  
                }
                #endif
            }

            return result;
        }

        /// <summary>
        /// Returns the bool value from ASK query forms.
        /// </summary>
        /// <returns>True on success, False otherwise.</returns>
        public bool GetAnswer()
        {
            using (var queryResults = _store.ExecuteQuery(_store.CreateQuery(_query), _transaction))
            {

                if (queryResults.Rows.Count > 0)
                {
                    return ((int) queryResults.Rows[0][0] != 0);
                }

                return false;
            }
        }

        public int Count()
        {
            var countQuery = SparqlSerializer.SerializeCount(_model, _query);

            var query = new SparqlQuery(countQuery)
            {
                IsInferenceEnabled = _query.IsInferenceEnabled
            };

            var q = _store.CreateQuery(query);

            foreach (var b in GenerateBindings(_store.ExecuteQuery(q)))
            {
                return (int)b["count"];
            }

            return -1;
        }

        public IEnumerable<Resource> GetResources(int offset = -1, int limit = -1)
        {
            return GetResources<Resource>(offset, limit);
        }

        public IEnumerable<T> GetResources<T>(int offset = -1, int limit = -1) where T : Resource
        {
            if (!_query.ProvidesStatements())
            {
                throw new ArgumentException("Error: The given SELECT query cannot be resolved into statements.");
            }

            if (!_query.IsInferenceEnabled)
            {
                var queryString = SparqlSerializer.SerializeOffsetLimit(_model, _query, offset, limit);

                var query = new SparqlQuery(queryString);

                using (var queryResults = _store.ExecuteQuery(_store.CreateQuery(query), _transaction))
                {
                   
                  return GenerateResources(typeof(T), queryResults).Where(x => typeof(T).IsAssignableFrom(x.GetType())).Select(x => x as T);
                    
                }
            }
            else
            {
                return GetResourcesWithInferencing<T>(offset, limit);
            }
        }

        private IEnumerable<T> GetResourcesWithInferencing<T>(int offset = -1, int limit = -1) where T : Resource
        {
            // TODO: Make resources which are returned from a inferenced query read-only in order to improve query performance.

            // NOTE: When inferencing is enabled, we are unable to determine which triples were inferred and
            // which not. Therefore we need to issue a query to get the URIs of all the resources the original
            // query would return and issue another query to describe those resources withoud inference.
            var uris = FetchUris(offset, limit).ToList();

            if (uris.Count.Equals(0)) yield break;
            
            var queryBuilder = new StringBuilder();

            foreach (Uri uri in uris)
            {
                queryBuilder.Append(SparqlSerializer.SerializeUri(uri));
            }

            var query = new SparqlQuery($"DESCRIBE {queryBuilder}");

            var queryResult = _model.ExecuteQuery(query);

            if (_isOrdered)
            {
                foreach (var t in queryResult.GetResources<T>().OrderBy(o => uris.IndexOf(o.Uri)))
                {
                    yield return t;
                }
            }
            else
            {
                foreach (var t in queryResult.GetResources<T>())
                {
                    yield return t;
                }
            }
        }

        /// <summary>
        /// Returns marshalled Resource objects returned from DESCRIBE, CONSTRUCT 
        /// or interpretable SELECT query forms.
        /// </summary>
        /// <returns>An enumeration of Resource objects.</returns>
        public IEnumerable<Resource> GetResources()
        {
            return GetResources<Resource>();
        }

        public IEnumerable<Resource> GetResources(Type type)
        {
            if (!_query.ProvidesStatements())
                throw new ArgumentException("The given query cannot be resolved into statements.");
            using (var queryResults = _store.ExecuteQuery(_store.CreateQuery(_query), _transaction))
            {
                return GenerateResources(type, queryResults);
            }
        }

        /// <summary>
        /// Returns marshalled instances of the given Resource type which were 
        /// returned from DESCRIBE, CONSTRUCT or interpretable SELECT query forms.
        /// </summary>
        /// <typeparam name="T">The Resource type object.</typeparam>
        /// <returns>An enumeration of instances of the given type.</returns>
        public IEnumerable<T> GetResources<T>() where T : Resource
        {
            if (!_query.ProvidesStatements())
                throw new ArgumentException("Error: The given SELECT query cannot be resolved into statements.");
            
            using (var queryResults = _store.ExecuteQuery(_store.CreateQuery(_query), _transaction))
            {
                return GenerateResources(typeof(T), queryResults).OfType<T>();
            }

        }

        /// <summary>
        /// Returns a set of bound values (bindings) returned from SELECT query forms.
        /// </summary>
        /// <returns>An enumeration of bound solution variables (BindingSet).</returns>
        public IEnumerable<BindingSet> GetBindings()
        {
            var queryString = "";
            try
            {
                queryString = _store.CreateQuery(_query);
                using (var queryResults = _store.ExecuteQuery(queryString, _transaction))
                {
                    return GenerateBindings(queryResults);
                }
            }
            
            catch (Exception e)
            {
                #if DEBUG
                Debug.WriteLine(e);
                #endif
                throw new InvalidQueryException("The current query led to an error in Virtuoso. See inner exception for more details.", e, queryString);
            }
        }

        /// <remarks>
        /// It is important to return UriRefs for correct equality comparision of URIs with fragment identifiers.
        /// </remarks>
        IEnumerable<UriRef> FetchUris(int offset, int limit)
        {
            var queryString = SparqlSerializer.SerializeFetchUris(_model, _query, offset, limit);

            var query = new SparqlQuery(queryString) { IsInferenceEnabled = _query.IsInferenceEnabled };

            using (var queryResults = _store.ExecuteQuery(_store.CreateQuery(query), _transaction))
            {
                var bindings = GenerateBindings(queryResults);

                UriRef previousUri = null;

                foreach (var binding in bindings)
                {
                    var currentUri = binding[_query.GetGlobalScopeVariableNames()[0]] as UriRef;

                    if (currentUri == null) continue;

                    if (!currentUri.Equals(previousUri))
                    {
                        yield return currentUri;
                    }

                    previousUri = currentUri;
                }
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
        }

        #endregion
    }

    internal static class SqlExtendedStringExtensions
    {
        public static bool IsResource(this SqlExtendedString extendedString)
        {
            return extendedString.IriType == SqlExtendedStringType.IRI && extendedString.StrType == SqlExtendedStringType.IRI;
        }

        public static bool IsString(this SqlExtendedString extendedString)
        {
            return extendedString.IriType == SqlExtendedStringType.IRI && extendedString.StrType == SqlExtendedStringType.BNODE;
        }

        public static bool IsBlankId(this SqlExtendedString extendedString)
        {
            return extendedString.IriType == SqlExtendedStringType.BNODE;
        }
    }  
}
