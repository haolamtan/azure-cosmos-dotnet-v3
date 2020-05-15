﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Routing
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Monads;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Routing;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    /// <summary>
    /// Tests <see cref="IRoutingMapProviderExtensions"/> class.
    /// </summary>
    [TestClass]
    public class IRoutingMapProviderExtensionsTest
    {
        private sealed class MockRoutingMapProvider : IRoutingMapProvider
        {
            private readonly CollectionRoutingMap routingMap;

            public MockRoutingMapProvider(IList<PartitionKeyRange> ranges)
            {
                if (!CollectionRoutingMap.TryCreateCompleteRoutingMap(ranges.Select(r => Tuple.Create(r, (ServiceIdentity)null)), "", null, out this.routingMap))
                {
                    throw new InvalidOperationException("Failed to create routing map");
                }
            }

            public Task<IReadOnlyList<PartitionKeyRange>> GetOverlappingRangesAsync(
                string collectionIdOrNameBasedLink,
                Range<string> range)
            {
                return Task.FromResult(this.routingMap.GetOverlappingRanges(range));
            }

            public Task<TryCatch<IReadOnlyList<PartitionKeyRange>>> TryGetOverlappingRangesAsync(
                string collectionIdOrNameBasedLink,
                Range<string> range,
                bool forceRefresh = false)
            {
                return Task.FromResult(TryCatch<IReadOnlyList<PartitionKeyRange>>.FromResult(this.routingMap.GetOverlappingRanges(range)));
            }

            public async Task<PartitionKeyRange> GetPartitionKeyRangeByIdAsync(
                string collectionResourceId,
                string partitionKeyRangeId)
            {
                TryCatch<PartitionKeyRange> tryGetPartitionKeyRangeByIdAsync = await this.TryGetPartitionKeyRangeByIdAsync(
                    collectionResourceId,
                    partitionKeyRangeId);
                tryGetPartitionKeyRangeByIdAsync.ThrowIfFailed();

                return tryGetPartitionKeyRangeByIdAsync.Result;
            }

            public Task<TryCatch<PartitionKeyRange>> TryGetPartitionKeyRangeByIdAsync(
                string collectionResourceId,
                string partitionKeyRangeId,
                bool forceRefresh = false)
            {
                if (!this.routingMap.TryGetRangeByPartitionKeyRangeId(partitionKeyRangeId, out PartitionKeyRange partitionKeyRange))
                {
                    return Task.FromResult(TryCatch<PartitionKeyRange>.FromException(new NotFoundException()));
                }

                return Task.FromResult(TryCatch<PartitionKeyRange>.FromResult(partitionKeyRange));
            }
        }

        private readonly MockRoutingMapProvider routingMapProvider =
            new MockRoutingMapProvider(
                new[]
                    {
                        new PartitionKeyRange{MinInclusive = "",   MaxExclusive = "000A", Id="0"},
                        new PartitionKeyRange{MinInclusive = "000A", MaxExclusive = "000D", Id="1"},
                        new PartitionKeyRange{MinInclusive = "000D", MaxExclusive = "0012", Id="2"},
                        new PartitionKeyRange{MinInclusive = "0012", MaxExclusive = "0015", Id="3"},
                        new PartitionKeyRange{MinInclusive = "0015", MaxExclusive = "0020", Id="4"},
                        new PartitionKeyRange{MinInclusive = "0020", MaxExclusive = "0040", Id="5"},
                        new PartitionKeyRange{MinInclusive = "0040", MaxExclusive = "FF", Id="6"},
                    });

        /// <summary>
        /// Tests case when input is not sorted.
        /// </summary>
        [TestMethod]
        [Owner("padmaa")]
        public async Task TestNonSortedRanges()
        {
            IList<PartitionKeyRange> ranges = await this.routingMapProvider.GetOverlappingRangesAsync(
                "dbs/db1/colls/coll1",
                new[] { new Range<string>("0B", "0B", true, true), new Range<string>("0A", "0A", true, true) });

            Assert.AreEqual("6", string.Join(",", ranges.Select(r => r.Id)));
        }

        /// <summary>
        /// Tests case when input contains overlapping ranges.
        /// </summary>
        [TestMethod]
        [Owner("padmaa")]
        [ExpectedException(typeof(ArgumentException))]
        public async Task TestOverlappingRanges1()
        {
            await this.routingMapProvider.TryGetOverlappingRangesAsync(
                "dbs/db1/colls/coll1",
                new[] { new Range<string>("0A", "0D", true, true), new Range<string>("0B", "0E", true, true) });
        }

        /// <summary>
        /// Tests case when input contains overlapping ranges.
        /// </summary>
        [TestMethod]
        [Owner("padmaa")]
        [ExpectedException(typeof(ArgumentException))]
        public async Task TestOverlappingRanges2()
        {
            await this.routingMapProvider.TryGetOverlappingRangesAsync(
                "dbs/db1/colls/coll1",
                new[] { new Range<string>("0A", "0D", true, true), new Range<string>("0D", "0E", true, true) });
        }

        /// <summary>
        /// Tests case with various overlapping options.
        /// </summary>
        [TestMethod]
        [Owner("padmaa")]
        public async Task TestDuplicates()
        {
            {
                // Deep Copy Duplicate
                IList<PartitionKeyRange> ranges = await this.routingMapProvider.GetOverlappingRangesAsync(
                "dbs/db1/colls/coll1",
                new[]
                {
                    new Range<string>("", "FF", true, false),
                    // Duplicate
                    new Range<string>("", "FF", true, false),
                });

                Assert.AreEqual("0,1,2,3,4,5,6", string.Join(",", ranges.Select(r => r.Id)));
            }

            {
                // Shallow Copy Duplicate
                List<Range<string>> queryRanges = new List<Range<string>>()
                {
                    new Range<string>("", "FF", true, false),
                };
                queryRanges.Add(queryRanges.Last());

                IList<PartitionKeyRange> ranges = await this.routingMapProvider.GetOverlappingRangesAsync(
                    "dbs/db1/colls/coll1",
                    queryRanges);

                Assert.AreEqual("0,1,2,3,4,5,6", string.Join(",", ranges.Select(r => r.Id)));
            }
        }

        /// <summary>
        /// Tests case with various overlapping options.
        /// </summary>
        [TestMethod]
        [Owner("padmaa")]
        public async Task TestGetOverlappingRanges()
        {
            IList<PartitionKeyRange> ranges = await this.routingMapProvider.GetOverlappingRangesAsync(
                "dbs/db1/colls/coll1",
                new[]
                    {
                        new Range<string>("000B", "000E", true, false),
                        new Range<string>("000E", "000F", true, false),
                        new Range<string>("000F", "0010", true, true),
                        new Range<string>("0015", "0015", true, true)
                    });

            Assert.AreEqual("1,2,4", string.Join(",", ranges.Select(r => r.Id)));

            // query for minimal point
            ranges = await this.routingMapProvider.GetOverlappingRangesAsync(
                "dbs/db1/colls/coll1",
                new[]
                    {
                        new Range<string>("", "", true, true),
                    });

            Assert.AreEqual("0", string.Join(",", ranges.Select(r => r.Id)));

            // query for empty range
            ranges = await this.routingMapProvider.GetOverlappingRangesAsync(
                "dbs/db1/colls/coll1",
                new[]
                    {
                        new Range<string>("", "", true, false),
                    });

            Assert.AreEqual("", string.Join(",", ranges.Select(r => r.Id)));

            // entire range
            ranges = await this.routingMapProvider.GetOverlappingRangesAsync(
                "dbs/db1/colls/coll1",
                new[]
                    {
                        new Range<string>("", "FF", true, false),
                    });

            Assert.AreEqual("0,1,2,3,4,5,6", string.Join(",", ranges.Select(r => r.Id)));

            // matching range
            ranges = await this.routingMapProvider.GetOverlappingRangesAsync(
                "dbs/db1/colls/coll1",
                new[]
                    {
                        new Range<string>("0012", "0015", true, false),
                    });

            Assert.AreEqual("3", string.Join(",", ranges.Select(r => r.Id)));

            // matching range and a little bit more.
            ranges = await this.routingMapProvider.GetOverlappingRangesAsync(
                "dbs/db1/colls/coll1",
                new[]
                    {
                        new Range<string>("0012", "0015", false, true),
                    });

            Assert.AreEqual("3,4", string.Join(",", ranges.Select(r => r.Id)));
        }

    }
}
