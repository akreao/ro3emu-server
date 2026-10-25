using System.Reflection;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestFramework("Ro3Emu.Tests.SplitClassFramework", "Ro3Emu.Tests")]

namespace Ro3Emu.Tests;

/// <summary>
/// xUnit's runner with one change: a test class with many tests runs as several parts at once. xUnit runs the tests of
/// a class one after another, so a run of one big class (DungeonTests: 130 tests, 3 min) used one core. Every test
/// starts its own server and the test classes keep no state between tests, so the parts are independent. A named
/// collection (<c>[Collection]</c>) keeps its tests together.
/// </summary>
public sealed class SplitClassFramework(IMessageSink messageSink) : XunitTestFramework(messageSink)
{
    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName) =>
        new Executor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);

    private sealed class Executor(AssemblyName assemblyName, ISourceInformationProvider sourceInformationProvider,
        IMessageSink diagnosticMessageSink)
        : XunitTestFrameworkExecutor(assemblyName, sourceInformationProvider, diagnosticMessageSink)
    {
        protected override async void RunTestCases(IEnumerable<IXunitTestCase> testCases, IMessageSink executionMessageSink,
            ITestFrameworkExecutionOptions executionOptions)
        {
            using var runner = new Runner(TestAssembly, testCases, DiagnosticMessageSink, executionMessageSink, executionOptions);
            await runner.RunAsync();
        }
    }

    private sealed class Runner(ITestAssembly testAssembly, IEnumerable<IXunitTestCase> testCases,
        IMessageSink diagnosticMessageSink, IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions)
        : XunitTestAssemblyRunner(testAssembly, testCases, diagnosticMessageSink, executionMessageSink, executionOptions)
    {
        // Tests per part: enough that a part pays its startup (JIT of the class's helpers) for several tests.
        private const int PartSize = 16;

        protected override async Task<RunSummary> RunTestCollectionAsync(IMessageBus messageBus, ITestCollection testCollection,
            IEnumerable<IXunitTestCase> testCases, CancellationTokenSource cancellationTokenSource)
        {
            var cases = testCases.ToList();
            if (testCollection.CollectionDefinition is not null || cases.Count <= PartSize)
                return await base.RunTestCollectionAsync(messageBus, testCollection, cases, cancellationTokenSource);
            var parts = await Task.WhenAll(cases.Chunk(PartSize).Select(part =>
                base.RunTestCollectionAsync(messageBus, testCollection, part, cancellationTokenSource)));
            var summary = new RunSummary();
            foreach (var part in parts) summary.Aggregate(part);
            return summary;
        }
    }
}
