﻿using GraphQL.Types;
using SAHB.GraphQL.Client.Introspection.Validation;
using SAHB.GraphQL.Client.Testserver.Schemas;
using SAHB.GraphQL.Client.TestServer;
using SAHB.GraphQLClient.FieldBuilder;
using SAHB.GraphQLClient.Introspection;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace SAHB.GraphQL.Client.Introspection.Tests.Hello
{
    public class ValidationHelloNonNull : GraphQLQueryTestBase<ValidationHelloNonNull.GraphQLQuery>
    {
        public ValidationHelloNonNull(GraphQLWebApplicationFactory<GenericQuerySchema<GraphQLQuery>> factory) : base(factory)
        {
        }

        [Fact]
        public async Task Validate_Hello_Query_IsValid()
        {
            // Arrange
            var graphQLClient = this.GraphQLHttpClient;

            // Act
            var introspectionQuery = await graphQLClient.CreateQuery<GraphQLIntrospectionQuery>("http://localhost/graphql").Execute();
            var validationOutput = introspectionQuery.ValidateGraphQLType<TestHelloQuery>(GraphQLOperationType.Query);

            // Assert
            Assert.Empty(validationOutput);
        }

        [Theory]
        [InlineData(GraphQLOperationType.Mutation)]
        [InlineData(GraphQLOperationType.Subscription)]
        public async Task Validate_Hello_Query_Invalid_QueryType(GraphQLOperationType operationType)
        {
            // Arrange
            var graphQLClient = this.GraphQLHttpClient;

            // Act
            var introspectionQuery = await graphQLClient.CreateQuery<GraphQLIntrospectionQuery>("http://localhost/graphql").Execute();
            var validationOutput = introspectionQuery.ValidateGraphQLType<TestHelloQuery>(operationType);

            // Assert
            Assert.Single(validationOutput);
            Assert.Equal(ValidationType.Operation_Type_Not_Found, validationOutput.First().ValidationType);
        }

        [Fact]
        public async Task Validate_Hello_Query_Invalid_FieldName()
        {
            // Arrange
            var graphQLClient = this.GraphQLHttpClient;

            // Act
            var introspectionQuery = await graphQLClient.CreateQuery<GraphQLIntrospectionQuery>("http://localhost/graphql").Execute();
            var validationOutput = introspectionQuery.ValidateGraphQLType<TestInvalidHelloQuery>(GraphQLOperationType.Query);

            // Assert
            Assert.Single(validationOutput);
            Assert.Equal(ValidationType.Field_Not_Found, validationOutput.First().ValidationType);
        }

        public class GraphQLQuery : ObjectGraphType
        {
            public GraphQLQuery()
            {
                Field<NonNullGraphType<StringGraphType>>("hello", resolve: context => "query");
            }
        }

        private class TestHelloQuery
        {
            public string Hello { get; set; }
        }

        private class TestInvalidHelloQuery
        {
            public string NotHello { get; set; }
        }
    }
}
