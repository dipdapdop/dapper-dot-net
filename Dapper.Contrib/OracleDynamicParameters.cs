﻿#if !NETSTANDARD1_3 || NETSTANDARD2_0
//
// Fork of https://github.com/vijaysg
// https://gist.github.com/vijaysg/3096151
// Extends StackOverflow/Dapper
// + Caren Wong (CW), + Shaun McGrath (SIM)
//

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Oracle.ManagedDataAccess.Client; // not yet available .NetStandard / COREFX

namespace Dapper
{

    public class OracleDynamicParameters : SqlMapper.IDynamicParameters
    {
        private static readonly Dictionary<SqlMapper.Identity, Action<IDbCommand, object>> paramReaderCache =
            new Dictionary<SqlMapper.Identity, Action<IDbCommand, object>>();

        private readonly Dictionary<string, ParamInfo> parameters = new Dictionary<string, ParamInfo>();
        private List<object> templates;

        /// <summary>
        ///     construct a dynamic parameter bag
        /// </summary>
        public OracleDynamicParameters()
        {
        }

        /// <summary>
        ///     construct a dynamic parameter bag
        /// </summary>
        /// <param name="template">can be an anonymous type or a DynamicParameters bag</param>
        public OracleDynamicParameters(object template)
        {
            AddDynamicParams(template);
        }

        /// <summary>
        ///     All the names of the param in the bag, use Get to yank them out
        /// </summary>
        public IEnumerable<string> ParameterNames
        {
            get { return parameters.Select(p => p.Key); }
        }

        void SqlMapper.IDynamicParameters.AddParameters(IDbCommand command, SqlMapper.Identity identity)
        {
            AddParameters(command, identity);
        }

        /// <summary>
        ///     Append a whole object full of params to the dynamic
        ///     EG: AddDynamicParams(new {A = 1, B = 2}) // will add property A and B to the dynamic
        /// </summary>
        /// <param name="param"></param>
        public void AddDynamicParams(dynamic param)
        {
            var obj = param as object;
            if (obj != null)
            {
                var subDynamic = obj as OracleDynamicParameters;
                if (subDynamic == null)
                {
                    IEnumerable<KeyValuePair<string, object>> dictionary =
                        obj as IEnumerable<KeyValuePair<string, object>>;
                    if (dictionary == null)
                    {
                        templates = templates ?? new List<object>();
                        templates.Add(obj);
                    }
                    else
                    {
                        foreach (KeyValuePair<string, object> kvp in dictionary)
                        {
                            Add(kvp.Key, kvp.Value);
                        }
                    }
                }
                else
                {
                    if (subDynamic.parameters != null)
                    {
                        foreach (KeyValuePair<string, ParamInfo> kvp in subDynamic.parameters)
                        {
                            parameters.Add(kvp.Key, kvp.Value);
                        }
                    }

                    if (subDynamic.templates != null)
                    {
                        templates = templates ?? new List<object>();
                        foreach (var t in subDynamic.templates)
                        {
                            templates.Add(t);
                        }
                    }
                }
            }
        }

        /// <summary>
        ///     Add a parameter to this dynamic parameter list
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        /// <param name="dbType"></param>
        /// <param name="direction"></param>
        /// <param name="size"></param>
        /// <param name="isAssociativeArray"></param>
        public void Add(string name, object value = null, OracleDbType? dbType = null,
            ParameterDirection? direction = null, int? size = null, bool isAssociativeArray = false)
        {
            parameters[Clean(name)] = new ParamInfo
            {
                Name = name,
                Value = value,
                ParameterDirection = direction ?? ParameterDirection.Input,
                DbType = dbType,
                Size = size,
                IsAssociativeArray = isAssociativeArray
            };
        }

        private static string Clean(string name)
        {
            if (!string.IsNullOrEmpty(name))
            {
                switch (name[0])
                {
                    case '@':
                    case ':':
                    case '?':
                        return name.Substring(1);
                }
            }
            return name;
        }

        /// <summary>
        ///     Add all the parameters needed to the command just before it executes
        /// </summary>
        /// <param name="command">The raw command prior to execution</param>
        /// <param name="identity">Information about the query</param>
        protected void AddParameters(IDbCommand command, SqlMapper.Identity identity)
        {
            if (templates != null)
            {
                foreach (var template in templates)
                {
                    var newIdent = identity.ForDynamicParameters(template.GetType());
                    Action<IDbCommand, object> appender;

                    lock (paramReaderCache)
                    {
                        if (!paramReaderCache.TryGetValue(newIdent, out appender))
                        {
                            appender = SqlMapper.CreateParamInfoGenerator(newIdent, false, false);
                            paramReaderCache[newIdent] = appender;
                        }
                    }

                    appender(command, template);
                }
            }

            foreach (var param in parameters.Values)
            {
                var name = Clean(param.Name);
                var add = !((OracleCommand)command).Parameters.Contains(name);
                OracleParameter p;
                if (add)
                {
                    p = ((OracleCommand)command).CreateParameter();
                    p.ParameterName = name;
                }
                else
                {
                    p = ((OracleCommand)command).Parameters[name];
                }
                var val = param.Value;
                p.Value = val ?? DBNull.Value;
                p.Direction = param.ParameterDirection;
                var s = val as string;
                if (s != null)
                {
                    if (s.Length <= 4000)
                    {
                        p.Size = 4000;
                    }
                }
                if (param.Size != null)
                {
                    p.Size = param.Size.Value;
                }
                if (param.DbType != null)
                {
                    p.OracleDbType = param.DbType.Value;
                }
                if (add)
                {
                    command.Parameters.Add(p);

                    //CW: determine if passing array argument
                    if (param.IsAssociativeArray)
                    {
                        p.CollectionType = OracleCollectionType.PLSQLAssociativeArray;
                    }
                    param.AttachedParam = p;
                }
            }
        }

        /// <summary>
        ///     Get the value of a parameter
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="name"></param>
        /// <returns>The value, note DBNull.Value is not returned, instead the value is returned as null</returns>
        public T Get<T>(string name)
        {
            var val = parameters[Clean(name)].AttachedParam.Value;
            if (val == DBNull.Value)
            {
                if (default(T) != null)
                {
                    throw new ApplicationException("Attempting to cast a DBNull to a non nullable type!");
                }
                return default(T);
            }
            return (T)val;
        }

        private class ParamInfo
        {
            public string Name { get; set; }

            public object Value { get; set; }

            public ParameterDirection ParameterDirection { get; set; }

            public OracleDbType? DbType { get; set; }

            public int? Size { get; set; }

            public IDbDataParameter AttachedParam { get; set; }

            public bool IsAssociativeArray { get; set; }
        }
    }
}
#endif
