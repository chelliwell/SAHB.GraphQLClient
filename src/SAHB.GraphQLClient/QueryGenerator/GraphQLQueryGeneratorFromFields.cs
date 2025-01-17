﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SAHB.GraphQLClient.Internal;
using SAHB.GraphQLClient.Exceptions;
using SAHB.GraphQLClient.FieldBuilder;
using System.Reflection;
using System.Runtime.Serialization;

namespace SAHB.GraphQLClient.QueryGenerator
{
    // ReSharper disable once InconsistentNaming
    /// <inheritdoc />
    public class GraphQLQueryGeneratorFromFields : IGraphQLQueryGeneratorFromFields
    {
        /// <inheritdoc />
        public string GenerateQuery(GraphQLOperationType operationType, IEnumerable<GraphQLField> selectionSet, params GraphQLQueryArgument[] arguments)
        {
            switch (operationType)
            {
                case GraphQLOperationType.Query:
                case GraphQLOperationType.Mutation:
                case GraphQLOperationType.Subscription:
                    return GetQuery(operationType, selectionSet.ToList(), arguments);
            }

            throw new NotImplementedException($"Operation {operationType} not implemented");
        }

        [Obsolete("Please use GenerateQuery instead")]
        /// <inheritdoc />
        public string GetQuery(IEnumerable<GraphQLField> fields, params GraphQLQueryArgument[] arguments) => GenerateQuery(GraphQLOperationType.Query, fields, arguments);

        [Obsolete("Please use GenerateQuery instead")]
        /// <inheritdoc />
        public string GetMutation(IEnumerable<GraphQLField> fields, params GraphQLQueryArgument[] arguments) => GenerateQuery(GraphQLOperationType.Mutation, fields, arguments);

        private string GetQuery(GraphQLOperationType operationType, ICollection<GraphQLField> fields, params GraphQLQueryArgument[] queryArguments)
        {
            // Get arguments
            var readonlyArguments = GetArguments(fields, queryArguments);

            // Get query
            if (operationType == GraphQLOperationType.Subscription)
            {
                // Only support for one field
                if (fields.Count > 1)
                {
                    throw new NotSupportedException("Subscriptions does not support more than one selection");
                }
            }

            // Get queryType
            string queryType;
            switch (operationType)
            {
                case GraphQLOperationType.Query:
                    queryType = "query";
                    break;
                case GraphQLOperationType.Mutation:
                    queryType = "mutation";
                    break;
                case GraphQLOperationType.Subscription:
                    queryType = "subscription " + (string.IsNullOrWhiteSpace(fields.First().Alias) ? fields.First().Field : fields.First().Alias);
                    break;
                default:
                    throw new NotImplementedException($"Querytype {operationType} not implemented");
            }

            var query = GetGraphQLQuery(queryType, GetArguments(readonlyArguments), GenerateQueryForFields(fields, readonlyArguments));
            var request = GetQueryRequest(query, readonlyArguments);

            // Logging
            if (Logger != null && Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug($"Generated the GraphQL query {request} from the fields:{Environment.NewLine + string.Join(Environment.NewLine, fields)}");
            }

            return request;
        }

        private IReadOnlyDictionary<GraphQLFieldArguments, GraphQLQueryArgument> GetArguments(ICollection<GraphQLField> fields, params GraphQLQueryArgument[] queryArguments)
        {
            // Get all the arguments from the fields
            var fieldArguments = Helper.GetAllArgumentsFromFields(fields).ToList();

            // Create list of argument variables which was not found
            var variablesNotFoundInFields = queryArguments.ToList();

            // Create mapping for each argument field
            IDictionary<GraphQLFieldArguments, GraphQLQueryArgument> arguments = new Dictionary<GraphQLFieldArguments, GraphQLQueryArgument>();
            ICollection<GraphQLFieldArguments> argumentsNotSet = new Collection<GraphQLFieldArguments>();
            List<string> duplicateVariableNames = new List<string>();

            foreach (var field in fieldArguments)
            {
                foreach (var fieldArgument in field.Value)
                {
                    // Remove from variablesNotFoundInFields
                    variablesNotFoundInFields.RemoveAll(argument =>
                        (argument.Field == null || argument.Field == field.Key) && // Match on field name
                        argument.VariableName == fieldArgument.VariableName);

                    // Find matching query arguments
                    var queryArgument = queryArguments
                        .Where(argument =>
                            (argument.Field == null || argument.Field == field.Key) &&
                            argument.VariableName == fieldArgument.VariableName)
                        .Take(2)
                        .ToList();

                    // Find match for argument
                    switch (queryArgument.Count)
                    {
                        case 0:
                            // Set default value
                            if (fieldArgument.DefaultValue != null)
                            {
                                arguments.Add(fieldArgument, new GraphQLQueryArgument(fieldArgument.VariableName, fieldArgument.DefaultValue));
                                break;
                            }
                            // If no default was set we need to check if it was required
                            if (fieldArgument.IsRequired)
                            {
                                argumentsNotSet.Add(fieldArgument);
                            }
                            break;
                        case 1:
                            arguments.Add(fieldArgument, queryArgument[0]);
                            break;
                        default:
                            duplicateVariableNames.Add(queryArgument[0].VariableName);
                            break;
                    }
                }
            }

            // If any arguments was detected not set
            if (argumentsNotSet.Any())
            {
                throw new GraphQLArgumentsRequiredException(argumentsNotSet);
            }

            // If duplicate variable names has been detected
            if (duplicateVariableNames.Any())
            {
                throw new GraphQLDuplicateVariablesException(duplicateVariableNames);
            }

            // Check if any supplied arguments was not found in fields
            if (variablesNotFoundInFields.Any())
            {
                throw new GraphQLArgumentVariableNotFoundException(variablesNotFoundInFields);
            }

            // Detect any variable collision
            var variableCollisions = arguments
                .Where(e => !ShouldInlineArgument(e)) // If argument is inlined it will not be a problem
                .GroupBy(e => e.Key.VariableName)
                .Where(e => e.Count() > 1)
                .ToList();

            // Replace entire list with new arguments to prevent side effects
            if (variableCollisions.Any())
            {
                arguments = arguments.ToDictionary(
                    e => e.Key,
                    e => (GraphQLQueryArgument)new GraphQLQueryArgumentWithOverriddenVariable(e.Value));
            }

            // Update all variables
            foreach (var argumentVariableCollision in variableCollisions)
            {
                foreach (var item in argumentVariableCollision.Select((value, i) => new { i, value }))
                {
                    var argumentWithCollision = item.value;
                    var index = item.i;

                    argumentWithCollision.Value.VariableName += "_" + index;
                }
            }

            // Get readonly arguments
            var readonlyArguments = new ReadOnlyDictionary<GraphQLFieldArguments, GraphQLQueryArgument>(arguments);
            return readonlyArguments;
        }

        private bool ShouldInlineArgument(KeyValuePair<GraphQLFieldArguments, GraphQLQueryArgument> keyValuePair) =>
            ShouldInlineArgument(keyValuePair.Key, keyValuePair.Value);

        private bool ShouldInlineArgument(GraphQLFieldArguments fieldArgument, GraphQLQueryArgument queryArgument)
        {
            // If inline is forced we should just inline
            if (fieldArgument.InlineArgument.HasValue)
            {
                return fieldArgument.InlineArgument.Value;
            }

            // Else implicit inline if query argument is of specific types
            switch (queryArgument.ArgumentValue)
            {
                case string _:
                case bool _:
                case int _:
                case long _:
                case float _:
                case double _:
                    return true;
            }

            // Else don't inline
            return false;
        }

        private string GetArguments(IReadOnlyDictionary<GraphQLFieldArguments, GraphQLQueryArgument> arguments)
        {
            return string.Join(" ", arguments.Where(argument => !ShouldInlineArgument(argument)).Select(e => $"${e.Key.VariableName}:{e.Key.ArgumentType}"));
        }

        private string GenerateQueryForFields(IEnumerable<GraphQLField> fields, IReadOnlyDictionary<GraphQLFieldArguments, GraphQLQueryArgument> arguments)
        {
            var builder = new StringBuilder();
            AppendSelectionSet(builder, fields, arguments);
            return "{" + builder.ToString() + "}";
        }

        private void AppendSelectionSet(StringBuilder builder, IEnumerable<GraphQLField> selectionSet, IReadOnlyDictionary<GraphQLFieldArguments, GraphQLQueryArgument> arguments)
        {
            if (selectionSet.Any())
            {
                bool firstField = true;
                foreach (var field in selectionSet)
                {
                    if (!firstField)
                    {
                        builder.Append(" ");
                    }

                    // SelectionSet
                    AppendField(builder, field, arguments);

                    firstField = false;
                }
            }
        }

        private void AppendField(StringBuilder builder, GraphQLField field, IReadOnlyDictionary<GraphQLFieldArguments, GraphQLQueryArgument> arguments)
        {
            // Append alias and field
            // Format: alias:field or field
            AppendFieldName(builder, field);

            // Append arguments
            // Format: (argumentName:$VariableName argumentName:$VariableName)
            AppendArguments(builder, field, arguments);

            // Append directives
            // Format: @directive(argumentName:$VariableName argumentName:$VariableName)
            AppendDirectives(builder, field, arguments);

            // Append selectionSet
            // Format: {field field}
            AppendSelectionSet(builder, field, arguments);
        }

        private void AppendFieldName(StringBuilder builder, GraphQLField field)
        {
            // Append alias and field
            // Format: alias:field or field
            if (field.Alias == null || field.Alias.Equals(field.Field, StringComparison.OrdinalIgnoreCase))
            {
                builder.Append(field.Field);
            }
            else
            {
                builder.Append(field.Alias + ":" + field.Field);
            }
        }

        private void AppendArguments(StringBuilder builder, GraphQLField field, IReadOnlyDictionary<GraphQLFieldArguments, GraphQLQueryArgument> arguments)
        {
            // Append arguments
            // Format: (argumentName:$VariableName argumentName:$VariableName)
            var fieldArguments = field.Arguments?.ToDictionary(argument => argument,
                argument => arguments.FirstOrDefault(e => e.Key == argument).Value).Where(e => e.Value != null);
            if (fieldArguments?.Any() ?? false)
            {
                builder.Append("(");

                builder.Append(string.Join(" ",
                    fieldArguments.Select(
                        argument => argument.Key.ArgumentName + ":" +
                                    (ShouldInlineArgument(argument)
                                        ? GetArgumentValue(argument.Value.ArgumentValue)
                                        : "$" + argument.Key.VariableName))));

                builder.Append(")");
            }
        }

        private void AppendDirectives(StringBuilder builder, GraphQLField field, IReadOnlyDictionary<GraphQLFieldArguments, GraphQLQueryArgument> arguments)
        {
            // Append directives
            // Format: @directive(argumentName:$VariableName argumentName:$VariableName)
            if (field.Directives?.Any() ?? false)
            {
                foreach (var directive in field.Directives)
                {
                    builder.Append($" @{directive.DirectiveName}");

                    var directiveArguments = directive.Arguments?.ToDictionary(argument => argument,
                        argument => arguments.FirstOrDefault(e => e.Key == argument).Value).Where(e => e.Value != null);

                    if (directiveArguments?.Any() ?? false)
                    {
                        builder.Append("(");
                        builder.Append(string.Join(" ",
                            directiveArguments.Select(
                                argument => argument.Key.ArgumentName + ":" +
                                            (ShouldInlineArgument(argument)
                                                ? GetArgumentValue(argument.Value.ArgumentValue)
                                                : "$" + argument.Key.VariableName))));
                        builder.Append(")");
                    }
                }
            }
        }

        private void AppendSelectionSet(StringBuilder builder, GraphQLField field, IReadOnlyDictionary<GraphQLFieldArguments, GraphQLQueryArgument> arguments)
        {
            // Append selectionSet
            if ((field.SelectionSet?.Any() ?? false) || (field.TargetTypes?.Any() ?? false))
            {
                if (field.SelectionSet?.Any() ?? false)
                {
                    builder.Append("{");
                    // SelectionSet
                    AppendSelectionSet(builder, field.SelectionSet, arguments);
                }

                // Get other possible subTypes
                if (field.TargetTypes?.Any() ?? false)
                {
                    foreach (var possibleType in field.TargetTypes)
                    {
                        builder.Append($" ... on {possibleType.Key}");
                        builder.Append("{");
                        AppendSelectionSet(builder, possibleType.Value.SelectionSet, arguments);
                        builder.Append("}");
                    }
                }

                if (field.SelectionSet?.Any() ?? false)
                {
                    builder.Append("}");
                }
            }
        }

        private string GetArgumentValue(object argumentValue)
        {
            // Enums should according to GraphQL spec be serilized without quotes
            // https://graphql.github.io/graphql-spec/June2018/#sec-Enum-Value
            // See issue 77
            var type = argumentValue.GetType();
            var typeInfo = type.GetTypeInfo();
            if (typeInfo.IsEnum)
            {
                // Detect EnumMember attribute
                var enumName = Enum.GetName(type, argumentValue);
                var enumMemberAttribute = typeInfo.GetDeclaredField(enumName).GetCustomAttribute<EnumMemberAttribute>(true);
                return enumMemberAttribute?.Value ?? enumName;
            }

            return JsonConvert.SerializeObject(argumentValue);
        }

        private string GetGraphQLQuery(string queryType, string argumentVariableDeclaration, string fields)
        {
            // Get argument string
            if (!string.IsNullOrEmpty(argumentVariableDeclaration))
            {
                argumentVariableDeclaration = $"({argumentVariableDeclaration})";
            }

            // Return query
            return queryType + argumentVariableDeclaration + fields;
        }

        private string GetQueryRequest(string query, IReadOnlyDictionary<GraphQLFieldArguments, GraphQLQueryArgument> arguments)
        {
            var variables = arguments.Where(e => !ShouldInlineArgument(e)).ToArray();
            if (variables.Any())
            {
                return JsonConvert.SerializeObject(new { query, variables = variables.ToDictionary(e => e.Value.VariableName, e => e.Value.ArgumentValue) });
            }

            return JsonConvert.SerializeObject(new { query });
        }

        #region Logging

        private ILoggerFactory _loggerFactory;

        /// <summary>
        /// Contains a logger factory for the GraphQLHttpClient
        /// </summary>
        public ILoggerFactory LoggerFactory
        {
            internal get { return _loggerFactory; }
            set
            {
                _loggerFactory = value;
                if (_loggerFactory != null)
                {
                    Logger = _loggerFactory.CreateLogger<GraphQLQueryGeneratorFromFields>();
                }
            }
        }

        /// <summary>
        /// Contains the logger for the class
        /// </summary>
        private ILogger<GraphQLQueryGeneratorFromFields> Logger { get; set; }

        #endregion
    }
}
