using Marrow.Infrastructure.Messaging;
using Marrow.Kernel.Messaging;
using StackExchange.Redis;
using Xunit;
namespace Marrow.Tests.Messaging;
public sealed class MessagingMechanicsTests
{
    [Fact]
    public void Retry_delay_grows_exponentially_and_stays_within_the_jitter_band_and_the_cap()
    {
        var schedule = new RetrySchedule(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5), 0.2);
        var random = new Random(7);
        for (var attempt = 1; attempt <= 40; attempt++)
        {
            var ceiling = Math.Min(5000, 100 * Math.Pow(2, Math.Min(attempt - 1, 30)));
            for (var i = 0; i < 200; i++)
            {
                var ms = schedule.Delay(attempt, random.NextDouble()).TotalMilliseconds;
                Assert.InRange(ms, ceiling * 0.8 - 0.001, ceiling);
            }
        }
        Assert.Equal(TimeSpan.FromMilliseconds(100), schedule.Delay(1, 0));
        Assert.Equal(TimeSpan.FromMilliseconds(400), schedule.Delay(3, 0));
        Assert.Equal(TimeSpan.FromSeconds(5), schedule.Delay(int.MaxValue, 0));
    }
    [Theory]
    [InlineData(0, 1, 0.1)]
    [InlineData(10, 5, 0.1)]
    [InlineData(1, 5, 0.6)]
    [InlineData(1, 5, -0.1)]
    public void Retry_schedule_rejects_unbounded_settings(int baseMs, int maxMs, double jitter) =>
        Assert.ThrowsAny<ArgumentException>(() => new RetrySchedule(TimeSpan.FromMilliseconds(baseMs), TimeSpan.FromMilliseconds(maxMs), jitter));
    [Fact]
    public void Stream_entry_round_trips_every_outbox_field_and_rejects_malformed_entries()
    {
        var message = new OutboxMessage(Guid.NewGuid(), "test.thing", 2, "agg-1",
            new DateTimeOffset(2026, 11, 10, 12, 30, 15, 123, TimeSpan.Zero).AddTicks(4560), """{"x":"ünï"}""", "corr-1");
        var fields = StreamEntryCodec.Encode(message);
        Assert.Equal(message, StreamEntryCodec.TryDecode(fields));
        Assert.Null(StreamEntryCodec.TryDecode(fields[1..]));                                          // missing field
        Assert.Null(StreamEntryCodec.TryDecode([.. fields, new NameValueEntry("extra", "1")]));       // unknown field
        Assert.Null(StreamEntryCodec.TryDecode([new(StreamEntryCodec.EventId, "nope"), .. fields[1..]]));
        Assert.Null(StreamEntryCodec.TryDecode([.. fields[..2], new(StreamEntryCodec.EventVersion, "0"), .. fields[3..]]));
    }
    [Fact]
    public void Sanitized_errors_keep_only_the_exception_class_and_redis_error_code()
    {
        var server = new RedisServerException(RedisErrorKind.WrongType, CommandFlags.None,
            "WRONGTYPE Operation against a key holding the wrong kind of value secret-key");
        Assert.Equal("RedisServerException:WRONGTYPE", MessagingErrors.Sanitize(server));
        Assert.Equal("InvalidOperationException", MessagingErrors.Sanitize(new InvalidOperationException("token=abc")));
        Assert.True(MessagingErrors.IsTransportUnavailable(new RedisConnectionException(
            ConnectionFailureType.UnableToConnect, CommandFlags.None, "down", null, CommandStatus.Unknown)));
        Assert.False(MessagingErrors.IsTransportUnavailable(server));
    }
    [Theory]
    [InlineData(RedisErrorKind.Loading, "LOADING Redis is loading the dataset in memory", true)]
    [InlineData(RedisErrorKind.Busy, "BUSY Redis is busy running a script. You can only call SCRIPT KILL or SHUTDOWN NOSAVE.", true)]
    [InlineData(RedisErrorKind.Misconfigured, "MISCONF Errors writing to the AOF file: No space left on device", true)]
    [InlineData(RedisErrorKind.OutOfMemory, "OOM command not allowed when used memory > 'maxmemory'.", true)]
    [InlineData(RedisErrorKind.ReadOnly, "READONLY You can't write against a read only replica.", true)]
    [InlineData(RedisErrorKind.NoAuth, "NOAUTH Authentication required.", true)]
    [InlineData(RedisErrorKind.NoPermission, "NOPERM User has no permissions to run the 'xadd' command", true)]
    [InlineData(RedisErrorKind.WrongType, "WRONGTYPE Operation against a key holding the wrong kind of value", false)]
    [InlineData(RedisErrorKind.UnknownError, "ERR string exceeds maximum allowed size", false)]
    public void Server_wide_redis_error_replies_are_outages_not_message_failures(RedisErrorKind kind, string reply, bool outage) =>
        Assert.Equal(outage, MessagingErrors.IsTransportUnavailable(new RedisServerException(kind, CommandFlags.None, reply)));
    [Theory]
    [InlineData("Bad-Upper")]
    [InlineData("x; DROP")]
    [InlineData("")]
    [InlineData("a234567890123456789012345678901234567890123456789012345678901234")]
    public void Module_schema_names_are_strict_identifiers(string schema) =>
        Assert.Throws<ArgumentException>(() => new MessagingTables(schema));
}
