// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.ComponentModel;
using Newtonsoft.Json;
using Schema.Domain.MySQL;

namespace Schema.Domain.MariaDb
{
    /// <summary>
    /// A MariaDB index. Everything MySQL's has, plus the temporal key: a unique key or primary key whose last key part
    /// names one of the table's <see cref="MariaDbTable.Periods"/> and excludes rows whose periods overlap.
    /// </summary>
    public class MariaDbIndex : MySqlIndex
    {
        /// <summary>
        /// <c>WITHOUT OVERLAPS</c> (MariaDB 10.5.3+): the key's last part is an application-time period of the table, and
        /// two rows with the same values in the other parts may not have overlapping periods. On a primary key or
        /// unique key only. Below 10.5.3 the key is created over the period's end and start columns instead and a
        /// downgrade is recorded.
        /// </summary>
        [SchemaProperty(Description = "MariaDB 10.5.3+. On a primary key or unique key whose last IndexColumns part names one of the table's Periods: rows with the same other key values may not have overlapping periods (WITHOUT OVERLAPS). Below 10.5.3 the key is created over the period's end and start columns and a downgrade is recorded.")]
        [JsonProperty(Order = 110)]
        [DefaultValue(false)]
        public bool WithoutOverlaps { get; set; }
    }
}
