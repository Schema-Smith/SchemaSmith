// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json;

namespace Schema.Domain.SqlServer
{
    /// <summary>
    /// The view SQL Server creates beside a ledger table, joining it to its history, and the names of the four columns
    /// it adds. Every member is optional: one left out is the engine's default, and extraction writes only the members
    /// that differ from it, so a package that names none is the plain <c>LEDGER = ON</c> it always was.
    /// </summary>
    public class SqlServerLedgerView
    {
        [SchemaProperty(Description = "The view's schema. Omitted, the table's.")]
        [JsonProperty(Order = 1, NullValueHandling = NullValueHandling.Ignore)]
        public string Schema { get; set; }

        [SchemaProperty(Description = "The view's name. Omitted, <table>_Ledger.")]
        [JsonProperty(Order = 2, NullValueHandling = NullValueHandling.Ignore)]
        public string Name { get; set; }

        [SchemaProperty(Description = "Omitted, ledger_transaction_id.")]
        [JsonProperty(Order = 3, NullValueHandling = NullValueHandling.Ignore)]
        public string TransactionIdColumn { get; set; }

        [SchemaProperty(Description = "Omitted, ledger_sequence_number.")]
        [JsonProperty(Order = 4, NullValueHandling = NullValueHandling.Ignore)]
        public string SequenceNumberColumn { get; set; }

        [SchemaProperty(Description = "Omitted, ledger_operation_type.")]
        [JsonProperty(Order = 5, NullValueHandling = NullValueHandling.Ignore)]
        public string OperationTypeColumn { get; set; }

        [SchemaProperty(Description = "Omitted, ledger_operation_type_desc.")]
        [JsonProperty(Order = 6, NullValueHandling = NullValueHandling.Ignore)]
        public string OperationTypeDescColumn { get; set; }
    }
}
