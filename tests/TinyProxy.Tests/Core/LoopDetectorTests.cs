namespace TinyProxy.Tests.Core;

/// <summary>
/// Unit tests for LoopDetector.
/// Verifies tinyproxy-aligned loop detection based on endpoint reuse.
/// </summary>
public class LoopDetectorTests
{
    [Fact]
    public void IsLoopDetected_ReturnsFalse_WhenNoOutgoingEndpointRecorded()
    {
        var detector = new LoopDetector();
        var remote = new IPEndPoint(IPAddress.Parse("192.168.1.100"), 8888);

        var result = detector.IsLoopDetected(remote);

        Assert.False(result);
    }

    [Fact]
    public void IsLoopDetected_ReturnsTrue_WhenIncomingMatchesRecentRecordedEndpoint()
    {
        var detector = new LoopDetector();
        var endpoint = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 8888);

        detector.RecordOutgoingLocalEndpoint(endpoint);

        Assert.True(detector.IsLoopDetected(endpoint));
    }

    [Fact]
    public void IsLoopDetected_ReturnsFalse_WhenPortDiffers()
    {
        var detector = new LoopDetector();
        detector.RecordOutgoingLocalEndpoint(new IPEndPoint(IPAddress.Parse("10.0.0.5"), 8888));

        Assert.False(detector.IsLoopDetected(new IPEndPoint(IPAddress.Parse("10.0.0.5"), 8080)));
    }

    [Fact]
    public void IsLoopDetected_ReturnsFalse_WhenAddressDiffers()
    {
        var detector = new LoopDetector();
        detector.RecordOutgoingLocalEndpoint(new IPEndPoint(IPAddress.Parse("10.0.0.5"), 8888));

        Assert.False(detector.IsLoopDetected(new IPEndPoint(IPAddress.Parse("10.0.0.6"), 8888)));
    }

    [Fact]
    public void IsLoopDetected_ReturnsFalse_ForDnsEndpoint()
    {
        var detector = new LoopDetector();
        detector.RecordOutgoingLocalEndpoint(new IPEndPoint(IPAddress.Parse("10.0.0.5"), 8888));

        Assert.False(detector.IsLoopDetected(new DnsEndPoint("example.com", 8888)));
    }

    [Fact]
    public void IsLoopDetected_ReturnsFalse_AfterTimeoutExpires()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new TestClock(now);
        var detector = new LoopDetector(TimeSpan.FromSeconds(15), clock.UtcNow);
        var endpoint = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 8888);

        detector.RecordOutgoingLocalEndpoint(endpoint);
        clock.Advance(TimeSpan.FromSeconds(16));

        Assert.False(detector.IsLoopDetected(endpoint));
    }

    [Fact]
    public void Clear_ResetsDetectionState()
    {
        var detector = new LoopDetector();
        var endpoint = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 8888);

        detector.RecordOutgoingLocalEndpoint(endpoint);

        detector.Clear();

        Assert.False(detector.IsLoopDetected(endpoint));
    }

    private sealed class TestClock
    {
        private DateTimeOffset _now;

        public TestClock(DateTimeOffset start) => _now = start;
        public DateTimeOffset UtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
