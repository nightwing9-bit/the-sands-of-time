using System.Text.RegularExpressions;
namespace Marrow.Infrastructure.Messaging;
/// <summary>
/// Names the outbox and inbox tables of one owning module schema. Spec 03 creates no such tables: every domain adds
/// them in its own migration (<c>scripts/db/migrations/&lt;module&gt;/</c>) with exactly this column shape, so the
/// shared mechanics below work unchanged. No cross-schema foreign keys.
/// <code>
/// CREATE TABLE &lt;schema&gt;.outbox (
///   seq bigint GENERATED ALWAYS AS IDENTITY UNIQUE,
///   event_id uuid PRIMARY KEY,
///   event_type text NOT NULL, event_version integer NOT NULL CHECK (event_version &gt; 0),
///   aggregate_id text NOT NULL, occurred_utc timestamptz NOT NULL,
///   payload text NOT NULL, correlation_id text NOT NULL,
///   status text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','published','poisoned')),
///   attempts integer NOT NULL DEFAULT 0, next_attempt_at timestamptz NOT NULL DEFAULT now(),
///   lease_token uuid NULL, lease_until timestamptz NULL, last_error text NULL,
///   enqueued_at timestamptz NOT NULL DEFAULT now(), published_at timestamptz NULL, stream_entry_id text NULL,
///   poisoned_at timestamptz NULL);
/// CREATE INDEX ON &lt;schema&gt;.outbox (next_attempt_at, seq) WHERE status = 'pending';
/// CREATE TABLE &lt;schema&gt;.inbox (
///   consumer_id text NOT NULL, event_id uuid NOT NULL, received_utc timestamptz NOT NULL DEFAULT now(),
///   PRIMARY KEY (consumer_id, event_id));
/// </code>
/// </summary>
public sealed partial record MessagingTables
{
    public MessagingTables(string schema)
    {
        if (schema is null || !Identifier().IsMatch(schema))
        {
            throw new ArgumentException("Schema must be a lowercase SQL identifier (a-z, 0-9, _; at most 63 characters).", nameof(schema));
        }
        Schema = schema;
    }
    public string Schema { get; }
    internal string Outbox => $"\"{Schema}\".outbox";
    internal string Inbox => $"\"{Schema}\".inbox";
    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex Identifier();
}
