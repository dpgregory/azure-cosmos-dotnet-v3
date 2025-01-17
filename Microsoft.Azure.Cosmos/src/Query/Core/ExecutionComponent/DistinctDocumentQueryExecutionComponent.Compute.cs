﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------
namespace Microsoft.Azure.Cosmos.Query.Core.ExecutionComponent
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos;
    using Microsoft.Azure.Cosmos.CosmosElements;

    internal abstract partial class DistinctDocumentQueryExecutionComponent : DocumentQueryExecutionComponentBase
    {
        /// <summary>
        /// Compute implementation of DISTINCT.
        /// Here we never serialize the continuation token, but you can always retrieve it on demand with TryGetContinuationToken.
        /// </summary>
        private sealed class ComputeDistinctDocumentQueryExecutionComponent : DistinctDocumentQueryExecutionComponent
        {
            private static readonly string UseTryGetContinuationTokenMessage = $"Use {nameof(ComputeDistinctDocumentQueryExecutionComponent.TryGetContinuationToken)}";

            private ComputeDistinctDocumentQueryExecutionComponent(
                DistinctQueryType distinctQueryType,
                DistinctMap distinctMap,
                IDocumentQueryExecutionComponent source)
                : base(distinctMap, source)
            {
            }

            /// <summary>
            /// Creates an DistinctDocumentQueryExecutionComponent
            /// </summary>
            /// <param name="queryClient">The query client</param>
            /// <param name="requestContinuation">The continuation token.</param>
            /// <param name="createSourceCallback">The callback to create the source to drain from.</param>
            /// <param name="distinctQueryType">The type of distinct query.</param>
            /// <returns>A task to await on and in return </returns>
            public static async Task<DistinctDocumentQueryExecutionComponent> CreateAsync(
                CosmosQueryClient queryClient,
                string requestContinuation,
                Func<string, Task<IDocumentQueryExecutionComponent>> createSourceCallback,
                DistinctQueryType distinctQueryType)
            {
                DistinctContinuationToken distinctContinuationToken;
                if (requestContinuation != null)
                {
                    if (!DistinctContinuationToken.TryParse(requestContinuation, out distinctContinuationToken))
                    {
                        throw queryClient.CreateBadRequestException($"Invalid {nameof(DistinctContinuationToken)}: {requestContinuation}");
                    }
                }
                else
                {
                    distinctContinuationToken = new DistinctContinuationToken(sourceToken: null, distinctMapToken: null);
                }

                DistinctMap distinctMap = DistinctMap.Create(distinctQueryType, distinctContinuationToken.DistinctMapToken);
                IDocumentQueryExecutionComponent source = await createSourceCallback(distinctContinuationToken.SourceToken);
                return new ComputeDistinctDocumentQueryExecutionComponent(
                    distinctQueryType,
                    distinctMap,
                    source);
            }

            /// <summary>
            /// Drains a page of results returning only distinct elements.
            /// </summary>
            /// <param name="maxElements">The maximum number of items to drain.</param>
            /// <param name="cancellationToken">The cancellation token.</param>
            /// <returns>A page of distinct results.</returns>
            public override async Task<QueryResponseCore> DrainAsync(int maxElements, CancellationToken cancellationToken)
            {
                List<CosmosElement> distinctResults = new List<CosmosElement>();
                QueryResponseCore sourceResponse = await base.DrainAsync(maxElements, cancellationToken);

                if (!sourceResponse.IsSuccess)
                {
                    return sourceResponse;
                }

                foreach (CosmosElement document in sourceResponse.CosmosElements)
                {
                    if (this.distinctMap.Add(document, out UInt128 hash))
                    {
                        distinctResults.Add(document);
                    }
                }

                return QueryResponseCore.CreateSuccess(
                        result: distinctResults,
                        continuationToken: null,
                        disallowContinuationTokenMessage: ComputeDistinctDocumentQueryExecutionComponent.UseTryGetContinuationTokenMessage,
                        activityId: sourceResponse.ActivityId,
                        requestCharge: sourceResponse.RequestCharge,
                        diagnostics: sourceResponse.Diagnostics,
                        responseLengthBytes: sourceResponse.ResponseLengthBytes);
            }

            public override bool TryGetContinuationToken(out string continuationToken)
            {
                if (this.IsDone)
                {
                    continuationToken = null;
                    return true;
                }

                if (!this.Source.TryGetContinuationToken(out string sourceContinuationToken))
                {
                    continuationToken = default;
                    return false;
                }

                continuationToken = new DistinctContinuationToken(
                    sourceContinuationToken,
                    this.distinctMap.GetContinuationToken()).ToString();
                return true;
            }
        }
    }
}
