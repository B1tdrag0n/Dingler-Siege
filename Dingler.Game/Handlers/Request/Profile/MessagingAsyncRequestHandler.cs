extern alias HexGame;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using HexGame::Game.Shared.Campaign.Siege;
using HexGame::Game.Shared.Domain;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Handlers.Request.Profile;

// Siege, DataType 150000. Every Siege call is a Messaging.Request whose Envelope is UTF-8 JSON naming the operation in
// "RequestType". The reply is a Messaging.Response whose Envelope is the JSON of that operation's Response class; the
// client matches it to its request by request id, so the reply carries no RequestType. See notes/siege-protocol.md.
// Returning null sends nothing, which leaves the client waiting, so only operations we can answer return a response.
[Authenticated]
public sealed class MessagingAsyncRequestHandler : IRequestHandler<Messaging.Request, Messaging.Response?>
{
	// Fee rules from the client's rules text: 1000 gold / 50 platinum minimum bounty, attacking costs 40% of the
	// bounty, and of that fee 50% goes to the defender and 37.5% is added to the bounty (the rest is burned).
	private static readonly Messaging.FeeDetail Fees = new(1000, 50, 0.4f, 0.5f, 0.375f);

	// The client reads replies with Newtonsoft.Json: public fields, exact field names, and enums as names or numbers.
	// (The game DLL's own Newtonsoft copy can read here but fails to write outside Unity, so we write with System.Text.Json.)
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		IncludeFields = true,
		Converters = { new JsonStringEnumConverter() }
	};

	private readonly ILogger<MessagingAsyncRequestHandler>? _logger;

	public MessagingAsyncRequestHandler(ILogger<MessagingAsyncRequestHandler>? logger = null)
	{
		_logger = logger;
	}

	public Messaging.Response? HandleRequest(SessionContext context, Messaging.Request request)
	{
		try
		{
			var json = Encoding.UTF8.GetString(request.Envelope ?? []);
			var requestType = ReadRequestType(json);
			_logger?.LogDebug("Siege: {type} from {user}: {json}", requestType, context.UserName, json);

			object? reply = requestType switch
			{
				Messaging.ReqType_FeeInfo => FeeInfo(),
				Messaging.ReqType_QueryStatus_ActiveForUser => Query(Read<QueryRequest>(json)),
				Messaging.ReqType_QueryUserListing => UserListing(Read<UserListingRequest>(json)),
				_ => null
			};

			if (reply is null)
			{
				_logger?.LogWarning("Siege: {type} from {user} is not implemented yet, sending no reply", requestType,
					context.UserName);
				return null;
			}

			return new Messaging.Response { Envelope = JsonSerializer.SerializeToUtf8Bytes(reply, reply.GetType(), JsonOptions) };
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Siege: failed to handle request from {user}", context.UserName);
			return null;
		}
	}

	private static Messaging.FeeInfo.Response FeeInfo() => new() { Detail = Fees };

	// The public defender list on the Attack screen. Nobody can commit a defense yet, so it is always empty.
	private static Messaging.Query.Response Query(QueryRequest request) => new()
	{
		QuerySort = request.Sorting ?? [],
		QueryOffset = request.Offset,
		QueryCount = request.Count,
		Results = []
	};

	// The player's own defenses for the Defense screen. The client loops over Results without a null check.
	private static Messaging.UserListing.Response UserListing(UserListingRequest request) => new()
	{
		SiegeStatus = request.SiegeStatus,
		Offset = request.Offset,
		Count = request.Count,
		Results = Array.Empty<siege_bits>()
	};

	private static string? ReadRequestType(string json)
	{
		using var document = JsonDocument.Parse(json);
		return document.RootElement.TryGetProperty("RequestType", out var type) ? type.GetString() : null;
	}

	private static T Read<T>(string json) =>
		JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new JsonException($"Empty {typeof(T).Name} envelope");

	// Request shapes as the client writes them. The client's own classes only have constructors with differently named
	// parameters, which System.Text.Json can't fill, so these mirror their fields instead.
	private sealed record QueryRequest(Messaging.SortParam[]? Sorting, int Offset, int Count);

	private sealed record UserListingRequest(Messaging.UserListing.Status SiegeStatus, int Offset, int Count, ulong DebugReckId);
}
