// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.IO.Pipelines;
using System.Text.Json;
using System.Threading.Tasks;

namespace PlatformBenchmarks
{
    public partial class BenchmarkApplication
    {
        private async Task MultipleQueries(PipeWriter pipeWriter, int count)
        {
            OutputMultipleQueries(pipeWriter, await RawDb.LoadMultipleQueriesRows(count));
        }

        private static void OutputMultipleQueries<TWord>(PipeWriter pipeWriter, TWord[] rows)
        {
            var writer = GetWriter(pipeWriter, sizeHint: 160 * rows.Length); // in reality it's 152 for one

            writer.Write(_dbPreamble);

            var lengthWriter = writer;
            writer.Write(_contentLengthGap);

            // Date header
            writer.Write(DateHeader.HeaderBytes);

            writer.Commit();

            Utf8JsonWriter utf8JsonWriter = t_writer ??= new Utf8JsonWriter(pipeWriter, new JsonWriterOptions { SkipValidation = true });
            utf8JsonWriter.Reset(pipeWriter);

            // Body
            JsonSerializer.Serialize<TWord[]>(utf8JsonWriter, rows, SerializerOptions);

            // Content-Length
            lengthWriter.WriteNumeric((uint)utf8JsonWriter.BytesCommitted);
        }
    }
}
