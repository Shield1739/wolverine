using Wolverine.Transports;
using Wolverine.Pubsub.Internal;
using Wolverine.Runtime;
using Google.Cloud.PubSub.V1;
using JasperFx.Core;
using Wolverine.Configuration;
using Spectre.Console;
using Google.Api.Gax;
using System.Text.RegularExpressions;

namespace Wolverine.Pubsub;

public class PubsubTransport : BrokerTransport<PubsubEndpoint>, IAsyncDisposable {
    public const string ProtocolName = "pubsub";
    public const string ResponseName = "wlvrn.responses";
    public const string DeadLetterName = "wlvrn.dead-letter";

    public static Regex NameRegex = new("^(?!goog)[A-Za-z][A-Za-z0-9\\-_.~+%]{2,254}$");

    internal PublisherServiceApiClient? PublisherApiClient = null;
    internal SubscriberServiceApiClient? SubscriberApiClient = null;


    public readonly LightweightCache<string, PubsubTopic> Topics;
    public readonly List<PubsubSubscription> Subscriptions = new();

    public string ProjectId = string.Empty;
    public EmulatorDetection EmulatorDetection = EmulatorDetection.None;
    public bool EnableDeadLettering = false;

    /// <summary>
    /// Is this transport connection allowed to build and use response and retry queues
    /// for just this node?
    /// </summary>
    public bool SystemEndpointsEnabled = false;

    public PubsubTransport() : base(ProtocolName, "Google Cloud Pub/Sub") {
        IdentifierDelimiter = ".";
        Topics = new(name => new(name, this));
    }

    public PubsubTransport(string projectId) : this() {
        ProjectId = projectId;
    }

    public override async ValueTask ConnectAsync(IWolverineRuntime runtime) {
        var pubBuilder = new PublisherServiceApiClientBuilder {
            EmulatorDetection = EmulatorDetection
        };
        var subBuilder = new SubscriberServiceApiClientBuilder {
            EmulatorDetection = EmulatorDetection,
        };

        if (string.IsNullOrWhiteSpace(ProjectId)) throw new InvalidOperationException("Google Cloud Pub/Sub project id must be set before connecting");

        PublisherApiClient = await pubBuilder.BuildAsync();
        SubscriberApiClient = await subBuilder.BuildAsync();
    }

    public override Endpoint? ReplyEndpoint() {
        var endpoint = base.ReplyEndpoint();

        if (endpoint is PubsubSubscription e) return e.Topic;

        return null;
    }

    public override IEnumerable<PropertyColumn> DiagnosticColumns() {
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected override IEnumerable<Endpoint> explicitEndpoints() {
        foreach (var topic in Topics) yield return topic;
        foreach (var subscription in Subscriptions) yield return subscription;
    }

    protected override IEnumerable<PubsubEndpoint> endpoints() {
        if (EnableDeadLettering) {
            var dlNames = Subscriptions.Select(x => x.DeadLetterName).Where(x => x.IsNotEmpty()).Distinct().ToArray();

            foreach (var dlName in dlNames) {
                var dlSubscription = Topics[dlName!].FindOrCreateSubscription($"sub.{dlName}");

                dlSubscription.DeadLetterName = null;
                dlSubscription.PubsubOptions.DeadLetterPolicy = null;
            }
        }

        foreach (var topic in Topics) yield return topic;
        foreach (var subscription in Subscriptions) yield return subscription;
    }

    protected override PubsubEndpoint findEndpointByUri(Uri uri) {
        if (uri.Scheme != Protocol) throw new ArgumentOutOfRangeException(nameof(uri));

        if (uri.Segments.Length == 3) {
            var existing = Subscriptions.FirstOrDefault(x => x.Uri.OriginalString == uri.OriginalString);

            if (existing is not null) return existing;

            var topic = Topics[uri.Segments[1].TrimEnd('/')];
            var subscription = new PubsubSubscription(uri.Segments[2].TrimEnd('/'), topic, this);

            Subscriptions.Add(subscription);

            return subscription;
        }

        return Topics.FirstOrDefault(x => x.Uri.OriginalString == uri.OriginalString) ?? Topics[uri.Segments[1].TrimEnd('/')];
    }

    protected override void tryBuildSystemEndpoints(IWolverineRuntime runtime) {
        if (!SystemEndpointsEnabled) return;

        var responseName = $"{ResponseName}.{Math.Abs(runtime.DurabilitySettings.AssignedNodeNumber)}";
        var responseTopic = new PubsubTopic(responseName, this, EndpointRole.System);
        var responseSubscription = new PubsubSubscription($"sub.{responseName}", responseTopic, this, EndpointRole.System);

        responseSubscription.IsUsedForReplies = true;

        Topics[responseName] = responseTopic;
        Subscriptions.Add(responseSubscription);
    }
}
