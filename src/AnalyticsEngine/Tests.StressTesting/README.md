# Office 365 Activity Importer - Stress Testing

This console application provides stress testing capabilities for the Office365ActivityImporter.Engine project. It allows you to run various stress tests to detect memory leaks, performance bottlenecks, and other issues under load.

## Features

- **Interactive Menu System**: Choose from available stress tests via a simple console menu
- **Memory Monitoring**: Tracks memory usage throughout test execution
- **Configurable Load**: Customize test parameters to simulate different scenarios
- **Performance Metrics**: Reports throughput, duration, and memory statistics

## Available Stress Tests

### 1. ActivityAPI Import Stress Test

Tests the ActivityAPI importing pipeline with fake loaders to simulate real-world data ingestion.

**Configurable Parameters:**
- Number of import iterations (1-10,000)
- Reports per metadata load (1-1,000)
- Report summaries per time slot (1-100)
- Number of time slots (1-50)
- Max saves per batch (1-1,000)
- Force garbage collection after each iteration (Y/N)
- Verbose output (Y/N)

**What it tests:**
- Memory allocation and deallocation patterns
- Import pipeline throughput
- Batch processing efficiency
- Potential memory leaks over multiple iterations

## Usage

1. Build and run the project
2. Select a stress test from the menu
3. Configure test parameters as prompted
4. Review results including:
   - Total items processed
   - Duration and throughput (items/second)
   - Memory usage (initial, peak, final, delta)
   - Any errors or warnings

## Memory Leak Detection

The stress test automatically analyzes memory growth patterns:

- **Warning Threshold**: If memory grows by more than 50% from initial to final state, a warning is displayed
- **Recommendations**: Consider forcing GC between iterations to verify cleanup is working correctly

## Adding New Stress Tests

1. Create a new class in `StressTests/` that inherits from `BaseStressTest`
2. Implement the `Execute()` method to return a `StressTestResult`
3. Add the test to the menu in `Program.cs`

Example:
```csharp
public class MyNewStressTest : BaseStressTest
{
    protected override StressTestResult Execute()
    {
        // Your test implementation
        var result = new StressTestResult { Success = true };
        // ... perform test ...
        return result;
    }
}
```

## Dependencies

- **WebJob.Office365ActivityImporter.Engine**: Core import functionality
- **Tests.UnitTests**: Access to fake loaders and test utilities
- **Common.Entities**: Entity models
- **Common.DataUtils**: Utility classes

## Best Practices

1. Start with lower iteration counts to establish baselines
2. Monitor Task Manager or Resource Monitor during long-running tests
3. Use verbose mode for debugging specific issues
4. Enable GC collection between iterations to verify proper cleanup
5. Run tests multiple times to ensure consistent results

## Troubleshooting

**High Memory Usage**:
- Reduce batch sizes
- Enable forced GC between iterations
- Check for unclosed connections or undisposed resources

**Slow Performance**:
- Reduce parallel thread count
- Optimize batch processing size
- Check for database connection bottlenecks

## Contributing

When adding new stress tests, ensure they:
- Include configurable parameters
- Track memory usage via `MemoryMonitor`
- Return comprehensive `StressTestResult` data
- Handle exceptions gracefully
- Provide meaningful progress output
