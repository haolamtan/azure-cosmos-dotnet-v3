﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
// ------------------------------------------------------------

namespace Microsoft.Azure.Cosmos
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Monads;
    using Microsoft.Azure.Cosmos.Query.Core.ContinuationTokens;
    using Newtonsoft.Json;

    /// <summary>
    /// FeedRangeContinuation using Composite Continuation Tokens and split proof.
    /// It uses a breath-first approach to transverse Composite Continuation Tokens.
    /// </summary>
    [JsonConverter(typeof(FeedRangeCompositeContinuationConverter))]
    internal sealed class FeedRangeCompositeContinuation : FeedRangeContinuation
    {
        public readonly Queue<CompositeContinuationToken> CompositeContinuationTokens;
        public CompositeContinuationToken CurrentToken { get; private set; }
        private static Documents.ShouldRetryResult Retry = Documents.ShouldRetryResult.RetryAfter(TimeSpan.Zero);
        private static Documents.ShouldRetryResult NoRetry = Documents.ShouldRetryResult.NoRetry();
        private readonly HashSet<string> doneRanges;
        private string initialNoResultsRange;

        private FeedRangeCompositeContinuation(
            string containerRid,
            FeedRangeInternal feedRange)
            : base(containerRid, feedRange)
        {
            this.CompositeContinuationTokens = new Queue<CompositeContinuationToken>();
            this.doneRanges = new HashSet<string>();
        }

        public override void Accept(
            FeedRangeVisitor visitor,
            Action<RequestMessage, string> fillContinuation)
        {
            visitor.Visit(this, fillContinuation);
        }

        public FeedRangeCompositeContinuation(
            string containerRid,
            FeedRangeInternal feedRange,
            IReadOnlyList<Documents.Routing.Range<string>> ranges,
            string continuation = null)
            : this(containerRid, feedRange)
        {
            if (ranges == null)
            {
                throw new ArgumentNullException(nameof(ranges));
            }

            if (ranges.Count == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(ranges));
            }

            foreach (Documents.Routing.Range<string> range in ranges)
            {
                this.CompositeContinuationTokens.Enqueue(
                    FeedRangeCompositeContinuation.CreateCompositeContinuationTokenForRange(
                        range.Min,
                        range.Max,
                        continuation));
            }

            this.CurrentToken = this.CompositeContinuationTokens.Peek();
        }

        /// <summary>
        /// Used for deserialization only
        /// </summary>
        public FeedRangeCompositeContinuation(
            string containerRid,
            FeedRangeInternal feedRange,
            IReadOnlyList<CompositeContinuationToken> deserializedTokens)
            : this(containerRid, feedRange)
        {
            if (deserializedTokens == null)
            {
                throw new ArgumentNullException(nameof(deserializedTokens));
            }

            if (deserializedTokens.Count == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(deserializedTokens));
            }

            foreach (CompositeContinuationToken token in deserializedTokens)
            {
                this.CompositeContinuationTokens.Enqueue(token);
            }

            this.CurrentToken = this.CompositeContinuationTokens.Peek();
        }

        public override string GetContinuation() => this.CurrentToken.Token;

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }

        public override void ReplaceContinuation(string continuationToken)
        {
            if (continuationToken == null)
            {
                // Normal ReadFeed can signal termination by CT null, not NotModified
                // Change Feed never lands here, as it always provides a CT
                // Consider current range done, if this FeedToken contains multiple ranges due to splits, all of them need to be considered done
                this.doneRanges.Add(this.CurrentToken.Range.Min);
            }

            this.CurrentToken.Token = continuationToken;
            this.MoveToNextToken();
        }

        public override TryCatch ValidateContainer(string containerRid)
        {
            if (!string.IsNullOrEmpty(this.ContainerRid) &&
                !this.ContainerRid.Equals(containerRid, StringComparison.Ordinal))
            {
                return TryCatch.FromException(
                    new ArgumentException(
                        string.Format(
                            ClientResources.FeedToken_InvalidFeedTokenForContainer,
                            this.ContainerRid,
                            containerRid)));
            }

            return TryCatch.FromResult();
        }

        /// <summary>
        /// The concept of Done is only for ReadFeed. Change Feed is never done, it is an infinite stream.
        /// </summary>
        public override bool IsDone => this.doneRanges.Count == this.CompositeContinuationTokens.Count;

        public override Documents.ShouldRetryResult HandleChangeFeedNotModified(ResponseMessage responseMessage)
        {
            if (responseMessage.IsSuccessStatusCode)
            {
                this.initialNoResultsRange = null;
                return FeedRangeCompositeContinuation.NoRetry;
            }

            // If the current response is NotModified (ChangeFeed), try and skip to a next range in case this continuation contains multiple ranges due to split (Breath-First)
            if (responseMessage.StatusCode == HttpStatusCode.NotModified
                && this.CompositeContinuationTokens.Count > 1)
            {
                if (this.initialNoResultsRange == null)
                {
                    this.initialNoResultsRange = this.CurrentToken.Range.Min;
                    this.ReplaceContinuation(responseMessage.Headers.ETag);
                    return FeedRangeCompositeContinuation.Retry;
                }

                if (!this.initialNoResultsRange.Equals(this.CurrentToken.Range.Min, StringComparison.OrdinalIgnoreCase))
                {
                    this.ReplaceContinuation(responseMessage.Headers.ETag);
                    return FeedRangeCompositeContinuation.Retry;
                }
            }

            return FeedRangeCompositeContinuation.NoRetry;
        }

        public override async Task<Documents.ShouldRetryResult> HandleSplitAsync(
            ContainerInternal containerCore,
            ResponseMessage responseMessage,
            CancellationToken cancellationToken)
        {
            // Split handling
            bool partitionSplit = responseMessage.StatusCode == HttpStatusCode.Gone
                && (responseMessage.Headers.SubStatusCode == Documents.SubStatusCodes.PartitionKeyRangeGone || responseMessage.Headers.SubStatusCode == Documents.SubStatusCodes.CompletingSplit);
            if (!partitionSplit)
            {
                return FeedRangeCompositeContinuation.NoRetry;
            }

            Routing.PartitionKeyRangeCache partitionKeyRangeCache = await containerCore.ClientContext.DocumentClient.GetPartitionKeyRangeCacheAsync();
            IReadOnlyList<Documents.PartitionKeyRange> resolvedRanges = await this.GetOverlappingRangesAsync(
                partitionKeyRangeCache,
                this.CurrentToken.Range.Min,
                this.CurrentToken.Range.Max,
                forceRefresh: true);
            this.CreateChildRanges(resolvedRanges);

            return FeedRangeCompositeContinuation.Retry;
        }

        public static new bool TryParse(string toStringValue, out FeedRangeContinuation feedToken)
        {
            try
            {
                feedToken = JsonConvert.DeserializeObject<FeedRangeCompositeContinuation>(toStringValue);
                return true;
            }
            catch (JsonReaderException)
            {
                feedToken = null;
                return false;
            }
        }

        private static CompositeContinuationToken CreateCompositeContinuationTokenForRange(
            string minInclusive,
            string maxExclusive,
            string token)
        {
            return new CompositeContinuationToken()
            {
                Range = new Documents.Routing.Range<string>(minInclusive, maxExclusive, true, false),
                Token = token
            };
        }

        private void MoveToNextToken()
        {
            CompositeContinuationToken recentToken = this.CompositeContinuationTokens.Dequeue();
            this.CompositeContinuationTokens.Enqueue(recentToken);
            this.CurrentToken = this.CompositeContinuationTokens.Peek();

            // In a Query / ReadFeed not Change Feed, skip ranges that are done to avoid requests
            while (!this.IsDone &&
                this.doneRanges.Contains(this.CurrentToken.Range.Min))
            {
                this.MoveToNextToken();
            }
        }

        private void CreateChildRanges(IReadOnlyList<Documents.PartitionKeyRange> keyRanges)
        {
            if (keyRanges == null) throw new ArgumentNullException(nameof(keyRanges));

            // Update current
            Documents.PartitionKeyRange firstRange = keyRanges[0];
            this.CurrentToken.Range = new Documents.Routing.Range<string>(firstRange.MinInclusive, firstRange.MaxExclusive, true, false);
            // Add children
            foreach (Documents.PartitionKeyRange keyRange in keyRanges.Skip(1))
            {
                this.CompositeContinuationTokens.Enqueue(
                    FeedRangeCompositeContinuation.CreateCompositeContinuationTokenForRange(
                        keyRange.MinInclusive,
                        keyRange.MaxExclusive,
                        this.CurrentToken.Token));
            }
        }

        private async Task<IReadOnlyList<Documents.PartitionKeyRange>> GetOverlappingRangesAsync(
            Routing.PartitionKeyRangeCache partitionKeyRangeCache,
            string min,
            string max,
            bool forceRefresh = false)
        {
            TryCatch<IReadOnlyList<Documents.PartitionKeyRange>> tryGetOverlappingRangesAsync = await partitionKeyRangeCache.TryGetOverlappingRangesAsync(
                this.ContainerRid,
                new Documents.Routing.Range<string>(
                    min,
                    max,
                    isMaxInclusive: false,
                    isMinInclusive: true),
                forceRefresh);
            tryGetOverlappingRangesAsync.ThrowIfFailed();

            if (tryGetOverlappingRangesAsync.Result.Count == 0)
            {
                throw new ArgumentOutOfRangeException("RequestContinuation", $"Token contains invalid range {min}-{max}");
            }

            return tryGetOverlappingRangesAsync.Result;
        }
    }
}
