﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------
namespace Microsoft.Azure.Cosmos.Query.Core.ExecutionComponent
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.CosmosElements;
    using Microsoft.Azure.Cosmos.Json;
    using Microsoft.Azure.Cosmos.Query.Core.ExecutionContext;

    /// <summary>
    /// Query execution component that groups groupings across continuations and pages.
    /// The general idea is a query gets rewritten from this:
    /// 
    /// SELECT c.team, c.name, COUNT(1) AS count
    /// FROM c
    /// GROUP BY c.team, c.name
    /// 
    /// To this:
    /// 
    /// SELECT 
    ///     [{"item": c.team}, {"item": c.name}] AS groupByItems, 
    ///     {"team": c.team, "name": c.name, "count": {"item": COUNT(1)}} AS payload
    /// FROM c
    /// GROUP BY c.team, c.name
    /// 
    /// With the following dictionary:
    /// 
    /// {
    ///     "team": null,
    ///     "name": null,
    ///     "count" COUNT
    /// }
    /// 
    /// So we know how to aggregate each column. 
    /// At the end the columns are stitched together to make the grouped document.
    /// </summary>
    internal abstract partial class GroupByDocumentQueryExecutionComponent : DocumentQueryExecutionComponentBase
    {
        private readonly GroupingTable groupingTable;

        protected GroupByDocumentQueryExecutionComponent(
            IDocumentQueryExecutionComponent source,
            GroupingTable groupingTable)
            : base(source)
        {
            if (groupingTable == null)
            {
                throw new ArgumentNullException(nameof(groupingTable));
            }

            this.groupingTable = groupingTable;
        }

        public override bool IsDone => this.groupingTable.IsDone;

        public static async Task<IDocumentQueryExecutionComponent> CreateAsync(
            ExecutionEnvironment executionEnvironment,
            CosmosQueryClient cosmosQueryClient,
            string requestContinuation,
            Func<string, Task<IDocumentQueryExecutionComponent>> createSourceCallback,
            IReadOnlyDictionary<string, AggregateOperator?> groupByAliasToAggregateType,
            IReadOnlyList<string> orderedAliases,
            bool hasSelectValue)
        {
            IDocumentQueryExecutionComponent groupByDocumentQueryExecutionComponent;
            switch (executionEnvironment)
            {
                case ExecutionEnvironment.Client:
                    groupByDocumentQueryExecutionComponent = await ClientGroupByDocumentQueryExecutionComponent.CreateAsync(
                        cosmosQueryClient,
                        requestContinuation,
                        createSourceCallback,
                        groupByAliasToAggregateType,
                        orderedAliases,
                        hasSelectValue);
                    break;

                case ExecutionEnvironment.Compute:
                    groupByDocumentQueryExecutionComponent = await ComputeGroupByDocumentQueryExecutionComponent.CreateAsync(
                        cosmosQueryClient,
                        requestContinuation,
                        createSourceCallback,
                        groupByAliasToAggregateType,
                        orderedAliases,
                        hasSelectValue);
                    break;

                default:
                    throw new ArgumentException($"Unknown {nameof(ExecutionEnvironment)}: {executionEnvironment}.");
            }

            return groupByDocumentQueryExecutionComponent;
        }

        protected void AggregateGroupings(IReadOnlyList<CosmosElement> cosmosElements)
        {
            foreach (CosmosElement result in cosmosElements)
            {
                // Aggregate the values for all groupings across all continuations.
                RewrittenGroupByProjection groupByItem = new RewrittenGroupByProjection(result);
                this.groupingTable.AddPayload(groupByItem);
            }
        }

        /// <summary>
        /// When a group by query gets rewritten the projection looks like:
        /// 
        /// SELECT 
        ///     [{"item": c.age}, {"item": c.name}] AS groupByItems, 
        ///     {"age": c.age, "name": c.name} AS payload
        /// 
        /// This struct just lets us easily access the "groupByItems" and "payload" property.
        /// </summary>
        protected readonly struct RewrittenGroupByProjection
        {
            private const string GroupByItemsPropertyName = "groupByItems";
            private const string PayloadPropertyName = "payload";

            private readonly CosmosObject cosmosObject;

            public RewrittenGroupByProjection(CosmosElement cosmosElement)
            {
                if (cosmosElement == null)
                {
                    throw new ArgumentNullException(nameof(cosmosElement));
                }

                if (!(cosmosElement is CosmosObject cosmosObject))
                {
                    throw new ArgumentException($"{nameof(cosmosElement)} must not be an object.");
                }

                this.cosmosObject = cosmosObject;
            }

            public CosmosArray GroupByItems
            {
                get
                {
                    if (!this.cosmosObject.TryGetValue(GroupByItemsPropertyName, out CosmosElement cosmosElement))
                    {
                        throw new InvalidOperationException($"Underlying object does not have an 'groupByItems' field.");
                    }

                    if (!(cosmosElement is CosmosArray cosmosArray))
                    {
                        throw new ArgumentException($"{nameof(RewrittenGroupByProjection)}['groupByItems'] was not an array.");
                    }

                    return cosmosArray;
                }
            }

            public CosmosElement Payload
            {
                get
                {
                    if (!this.cosmosObject.TryGetValue(PayloadPropertyName, out CosmosElement cosmosElement))
                    {
                        throw new InvalidOperationException($"Underlying object does not have an 'payload' field.");
                    }

                    return cosmosElement;
                }
            }
        }

        protected sealed class GroupingTable : IEnumerable<KeyValuePair<UInt128, SingleGroupAggregator>>
        {
            private static readonly AggregateOperator[] EmptyAggregateOperators = new AggregateOperator[] { };

            private readonly Dictionary<UInt128, SingleGroupAggregator> table;
            private readonly CosmosQueryClient cosmosQueryClient;
            private readonly IReadOnlyDictionary<string, AggregateOperator?> groupByAliasToAggregateType;
            private readonly IReadOnlyList<string> orderedAliases;
            private readonly bool hasSelectValue;

            private GroupingTable(
                CosmosQueryClient cosmosQueryClient,
                IReadOnlyDictionary<string, AggregateOperator?> groupByAliasToAggregateType,
                IReadOnlyList<string> orderedAliases,
                bool hasSelectValue)
            {
                if (cosmosQueryClient == null)
                {
                    throw new ArgumentNullException(nameof(cosmosQueryClient));
                }

                if (groupByAliasToAggregateType == null)
                {
                    throw new ArgumentNullException(nameof(groupByAliasToAggregateType));
                }

                this.cosmosQueryClient = cosmosQueryClient;
                this.groupByAliasToAggregateType = groupByAliasToAggregateType;
                this.orderedAliases = orderedAliases;
                this.hasSelectValue = hasSelectValue;
                this.table = new Dictionary<UInt128, SingleGroupAggregator>();
            }

            public int Count => this.table.Count;

            public bool IsDone { get; private set; }

            public void AddPayload(RewrittenGroupByProjection rewrittenGroupByProjection)
            {
                UInt128 groupByKeysHash = DistinctHash.GetHash(rewrittenGroupByProjection.GroupByItems);

                if (!this.table.TryGetValue(groupByKeysHash, out SingleGroupAggregator singleGroupAggregator))
                {
                    singleGroupAggregator = SingleGroupAggregator.Create(
                        this.cosmosQueryClient,
                        EmptyAggregateOperators,
                        this.groupByAliasToAggregateType,
                        this.orderedAliases,
                        this.hasSelectValue,
                        continuationToken: null);
                    this.table[groupByKeysHash] = singleGroupAggregator;
                }

                CosmosElement payload = rewrittenGroupByProjection.Payload;
                singleGroupAggregator.AddValues(payload);
            }

            public IReadOnlyList<CosmosElement> Drain(int maxItemCount)
            {
                List<UInt128> keys = this.table.Keys.Take(maxItemCount).ToList();
                List<SingleGroupAggregator> singleGroupAggregators = new List<SingleGroupAggregator>(maxItemCount);
                foreach (UInt128 key in keys)
                {
                    SingleGroupAggregator singleGroupAggregator = this.table[key];
                    singleGroupAggregators.Add(singleGroupAggregator);
                }

                foreach (UInt128 key in keys)
                {
                    this.table.Remove(key);
                }

                List<CosmosElement> results = new List<CosmosElement>();
                foreach (SingleGroupAggregator singleGroupAggregator in singleGroupAggregators)
                {
                    results.Add(singleGroupAggregator.GetResult());
                }

                if (this.Count == 0)
                {
                    this.IsDone = true;
                }

                return results;
            }

            public string GetContinuationToken()
            {
                IJsonWriter jsonWriter = JsonWriter.Create(JsonSerializationFormat.Text);
                jsonWriter.WriteObjectStart();
                foreach (KeyValuePair<UInt128, SingleGroupAggregator> kvp in this.table)
                {
                    jsonWriter.WriteFieldName(kvp.Key.ToString());
                    jsonWriter.WriteStringValue(kvp.Value.GetContinuationToken());
                }
                jsonWriter.WriteObjectEnd();

                string result = Utf8StringHelpers.ToString(jsonWriter.GetResult());
                return result;
            }

            public IEnumerator<KeyValuePair<UInt128, SingleGroupAggregator>> GetEnumerator => this.table.GetEnumerator();

            public static GroupingTable CreateFromContinuationToken(CosmosQueryClient cosmosQueryClient,
                IReadOnlyDictionary<string, AggregateOperator?> groupByAliasToAggregateType,
                IReadOnlyList<string> orderedAliases,
                bool hasSelectValue,
                string groupingTableContinuationToken)
            {
                GroupingTable groupingTable = new GroupingTable(
                    cosmosQueryClient,
                    groupByAliasToAggregateType,
                    orderedAliases,
                    hasSelectValue);

                if (groupingTableContinuationToken != null)
                {
                    if (!CosmosElement.TryParse(
                        groupingTableContinuationToken,
                        out CosmosObject parsedGroupingTableContinuations))
                    {
                        throw cosmosQueryClient.CreateBadRequestException($"Invalid GroupingTableContinuationToken");
                    }

                    foreach (KeyValuePair<string, CosmosElement> kvp in parsedGroupingTableContinuations)
                    {
                        string key = kvp.Key;
                        CosmosElement value = kvp.Value;

                        UInt128 groupByKey = UInt128.Parse(key);

                        if (!(value is CosmosString singleGroupAggregatorContinuationToken))
                        {
                            throw cosmosQueryClient.CreateBadRequestException($"Invalid GroupingTableContinuationToken");
                        }

                        SingleGroupAggregator singleGroupAggregator = SingleGroupAggregator.Create(
                            cosmosQueryClient,
                            EmptyAggregateOperators,
                            groupByAliasToAggregateType,
                            orderedAliases,
                            hasSelectValue,
                            singleGroupAggregatorContinuationToken.Value);

                        groupingTable.table[groupByKey] = singleGroupAggregator;
                    }
                }

                return groupingTable;
            }

            IEnumerator<KeyValuePair<UInt128, SingleGroupAggregator>> IEnumerable<KeyValuePair<UInt128, SingleGroupAggregator>>.GetEnumerator() => this.table.GetEnumerator();

            IEnumerator IEnumerable.GetEnumerator() => this.table.GetEnumerator();
        }
    }
}