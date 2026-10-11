extern alias HexGame;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dingler.Data.Entities.GameData;
using Dingler.Data.Enums;
using Dingler.Data.Repositories;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using HexGame::Game.Shared.Campaign.Siege;
using HexGame::Game.Shared.Domain;
using HexGame::Game.Shared.Network;
using HexGame::Game.Shared.Utils;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Handlers.Request.Profile;

// Siege, DataType 150000. Every Siege call is a Messaging.Request whose Envelope is UTF-8 JSON naming the operation in
// "RequestType". The reply is a Messaging.Response whose Envelope is the JSON of that operation's Response class; the
// client matches it to its request by request id, so the reply carries no RequestType. See notes/siege-protocol.md.
// A refusal is an ErrorArgs instead of a Messaging.Response: the client hands its ErrorMessage to the screen's error
// handler, which usually shows it in a popup. Every request gets one or the other, so no screen is left waiting.
[Authenticated]
public sealed class MessagingAsyncRequestHandler : IAsyncRequestHandler<Messaging.Request, IRequestArgs>
{
	// Fee rules from the client's rules text: 1000 gold / 50 platinum minimum bounty, attacking costs 40% of the
	// bounty, and of that fee 50% goes to the defender and 37.5% is added to the bounty (the rest is burned).
	private static readonly Messaging.FeeDetail Fees = new(1000, 50, 0.4f, 0.5f, 0.375f);

	private const int FightsPerDefense = 3;

	// Attacks are Milestone 5. Until then these get a clear refusal rather than silence.
	private static readonly HashSet<string> AttackRequests =
	[
		Messaging.ReqType_ChallengeListing, Messaging.ReqType_Forfeit, Messaging.ReqType_TestListing,
		Messaging.ReqType_PracticeGame, Messaging.ReqType_Lock
	];

	// The client reads replies with Newtonsoft.Json: public fields, exact field names, and enums as names or numbers.
	// Its Siege dates must be "yyyy-MM-dd HH:mm:ss", and a missing date is left out rather than sent as null.
	// (The game DLL's own Newtonsoft copy can read here but fails to write outside Unity, so we write with System.Text.Json.)
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		IncludeFields = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Converters = { new JsonStringEnumConverter(), new SiegeDateConverter() }
	};

	private readonly SiegeRepository _sieges;
	private readonly ILogger<MessagingAsyncRequestHandler>? _logger;

	public MessagingAsyncRequestHandler(SiegeRepository sieges, ILogger<MessagingAsyncRequestHandler>? logger = null)
	{
		_sieges = sieges;
		_logger = logger;
	}

	public async Task<IRequestArgs> HandleRequestAsync(SessionContext context, Messaging.Request request,
		CancellationToken token)
	{
		try
		{
			var json = Encoding.UTF8.GetString(request.Envelope ?? []);
			var requestType = ReadRequestType(json);
			_logger?.LogDebug("Siege: {type} from {user}: {json}", requestType, context.UserName, json);

			object reply = requestType switch
			{
				Messaging.ReqType_FeeInfo => FeeInfo(),
				Messaging.ReqType_QueryStatus_ActiveForUser => await Query(Read<QueryRequest>(json)),
				Messaging.ReqType_QueryUserListing => await UserListing(context, Read<UserListingRequest>(json)),
				Messaging.ReqType_QuerySiegeLog => await SiegeLog(Read<SiegeLogRequest>(json)),
				Messaging.ReqType_CreateListing => await Create(context, Read<CreateRequest>(json)),
				Messaging.ReqType_ActivateListing => await Activate(context, Read<SiegeIdRequest>(json)),
				Messaging.ReqType_RemoveListing => await Remove(context, Read<SiegeIdRequest>(json)),
				_ when requestType is not null && AttackRequests.Contains(requestType) =>
					Refuse("Siege attacks aren't available on this server yet."),
				_ => Refuse($"Unknown Siege request '{requestType}'.")
			};

			if (reply is ErrorArgs error)
			{
				_logger?.LogInformation("Siege: refused {type} from {user}: {reason}", requestType, context.UserName,
					error.ErrorMessage);
				return error;
			}

			return new Messaging.Response { Envelope = JsonSerializer.SerializeToUtf8Bytes(reply, reply.GetType(), JsonOptions) };
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Siege: failed to handle request from {user}", context.UserName);
			return Refuse("The Siege server hit an error. Please try again.");
		}
	}

	private static Messaging.FeeInfo.Response FeeInfo() => new() { Detail = Fees };

	// The public defender list on the Attack screen. The client sorts and filters it itself, so Sorting is only echoed.
	private async Task<object> Query(QueryRequest request) => new Messaging.Query.Response
	{
		QuerySort = request.Sorting ?? [],
		QueryOffset = request.Offset,
		QueryCount = request.Count,
		Results = Page(await _sieges.GetOpenAsync(), request.Offset, request.Count).Select(ToBits).ToArray()
	};

	// The player's own defenses for the Defense screen. The client loops over Results without a null check.
	private async Task<object> UserListing(SessionContext context, UserListingRequest request)
	{
		var defenses = (await _sieges.GetForPlayerAsync(context.ProfileId)).Where(d => request.SiegeStatus switch
		{
			Messaging.UserListing.Status.ACTIVE => IsOpen(d),
			Messaging.UserListing.Status.CLOSED => !IsOpen(d),
			_ => true
		});

		return new Messaging.UserListing.Response
		{
			SiegeStatus = request.SiegeStatus,
			Offset = request.Offset,
			Count = request.Count,
			Results = Page(defenses, request.Offset, request.Count).Select(ToBits).ToArray()
		};
	}

	// The events log. The Defense screen asks by PlayerId for "my latest defense"; by SiegeId for a given one. With no
	// defense, Siege is left out; Logs and Replays must still be lists because the client loops over both.
	private async Task<object> SiegeLog(SiegeLogRequest request)
	{
		var defense = request.SiegeId != 0
			? await _sieges.GetAsync(request.SiegeId)
			: await _sieges.GetLatestForPlayerAsync(request.PlayerId);

		return new Messaging.SiegeLog.Response
		{
			Siege = defense is null ? null! : ToBits(defense),
			Logs = defense is null ? [] : (await _sieges.GetLedgerAsync(defense.Id)).Select(ToBits).ToArray(),
			Replays = [] // Milestone 7
		};
	}

	// Commit Defense. The client already stops the player from committing with an empty slot, but the server can't
	// trust that, so it checks the decks and the bounty again before storing anything.
	private async Task<object> Create(SessionContext context, CreateRequest request)
	{
		var fights = request.Fights ?? [];
		if (fights.Length != FightsPerDefense || fights.Any(f => !IsPlayableDeck(f.Deck)))
			return Refuse($"A defense needs {FightsPerDefense} complete decks, each with a champion and cards.");

		var currency = (siege_bits.CurrencyType)request.Cur;
		var minimum = currency == siege_bits.CurrencyType.PLATINUM ? Fees.MinPotPlatinum : Fees.MinPotGold;
		if (request.PotAmount < minimum)
			return Refuse($"The bounty must be at least {minimum} {currency.ToString().ToLowerInvariant()}.");

		// No wallet yet (Milestone 4), so the bounty isn't taken from the player and the defense goes straight to ACTIVE.
		var now = Now();
		var playerName = context.UserName ?? "";
		var defense = await _sieges.TryCreateAsync(new Siege
		{
			PlayerProfileId = context.ProfileId,
			PlayerName = playerName,
			Currency = (SiegeCurrency)currency,
			Pot = request.PotAmount,
			ChallengeLimit = request.ChallengeLimit,
			FightInfo = PackFights(fights),
			Status = SiegeStatus.Active,
			Created = now,
			StatusDate = now
		}, new SiegeLedger
		{
			Action = SiegeAction.State,
			Context = nameof(siege_bits.Status.ACTIVE),
			PlayerProfileId = context.ProfileId,
			PlayerName = playerName,
			Created = now
		});
		if (defense is null)
			return Refuse("You already have an active defense. Withdraw it before committing a new one.");

		_logger?.LogInformation("Siege: {user} committed defense {id} with a {pot} {currency} bounty", context.UserName,
			defense.Id, defense.Pot, currency);
		return new Messaging.Create.Response { Updated = ToBits(defense) };
	}

	// The client never sends this today (create already returns an active defense), but answer it the same way.
	private async Task<object> Activate(SessionContext context, SiegeIdRequest request)
	{
		var defense = await _sieges.GetAsync(request.SiegeId);
		if (defense is null || defense.PlayerProfileId != context.ProfileId)
			return Refuse("That defense doesn't exist.");

		return new Messaging.Activate.Response { Updated = ToBits(defense) };
	}

	// Withdraw. The reply is the closed defense, which the client shows on its results screen.
	private async Task<object> Remove(SessionContext context, SiegeIdRequest request)
	{
		var defense = await _sieges.GetAsync(request.SiegeId);
		if (defense is null || defense.PlayerProfileId != context.ProfileId)
			return Refuse("That defense doesn't exist.");

		var closed = await _sieges.CloseAsync(defense.Id, Now());
		if (closed is null)
			return Refuse("That defense is already closed.");

		_logger?.LogInformation("Siege: {user} withdrew defense {id}", context.UserName, closed.Id);
		return new Messaging.Remove.Response { Updated = ToBits(closed) };
	}

	private static ErrorArgs Refuse(string message) => new() { Error = 1, ErrorMessage = message };

	// A deck counts if it has a champion and at least one card. The client sends the whole deck (Newtonsoft's view of
	// ProfileDeckTemplate): the champion as {"m_Guid": ...} in "Champ", the cards as [{Temp, Count, ...}] in "CardDesc".
	private static bool IsPlayableDeck(JsonElement? deck)
	{
		if (deck is not { ValueKind: JsonValueKind.Object } d)
			return false;

		var hasChampion = d.TryGetProperty("Champ", out var champ) && champ.ValueKind == JsonValueKind.Object &&
		                  champ.TryGetProperty("m_Guid", out var guid) && Guid.TryParse(guid.GetString(), out var id) &&
		                  id != Guid.Empty;

		var cardCount = d.TryGetProperty("CardDesc", out var cards) && cards.ValueKind == JsonValueKind.Array
			? cards.EnumerateArray().Sum(c => c.TryGetProperty("Count", out var n) && n.TryGetInt32(out var count) ? count : 0)
			: 0;

		return hasChampion && cardCount > 0;
	}

	// siege_bits.FightInfo is the gzipped JSON of the Create.FightConfig array, which the client unpacks to show the decks.
	// The decks are copied exactly as the client sent them, so the defense keeps its own snapshot of each deck.
	private static byte[] PackFights(FightRequest[] fights)
	{
		using var buffer = new MemoryStream();
		using (var writer = new Utf8JsonWriter(buffer))
		{
			writer.WriteStartArray();
			foreach (var fight in fights)
			{
				writer.WriteStartObject();
				writer.WritePropertyName("Deck");
				fight.Deck!.Value.WriteTo(writer);
				writer.WriteString("Message", fight.Message);
				writer.WriteEndObject();
			}
			writer.WriteEndArray();
		}

		return Compressor.Compress(buffer.ToArray());
	}

	private static siege_bits ToBits(Siege defense)
	{
		// What an attacker pays, split the way the rules text describes: a fee to the defender, a bet added to the
		// bounty, and the rest burned (the rake).
		var cost = (int)Math.Round(defense.Pot * Fees.ChallengeCostPotPct);
		var fee = (int)Math.Round(cost * Fees.ChallengePotFeePct);
		var bet = (int)Math.Round(cost * Fees.ChallengePotBetPct);

		return new siege_bits
		{
			Id = defense.Id,
			PlayerID = defense.PlayerProfileId,
			Name = defense.PlayerName,
			FightInfo = defense.FightInfo,
			CurStatus = (siege_bits.Status)defense.Status,
			StatusDate = defense.StatusDate,
			ChallengeCount = defense.ChallengeCount,
			Wins = defense.Wins,
			Loss = defense.Loss,
			Pot = defense.Pot,
			ChallengeLimit = defense.ChallengeLimit,
			ChallengeFee = fee,
			ChallengeBet = bet,
			ChallengeRake = cost - fee - bet,
			Currency = (siege_bits.CurrencyType)defense.Currency,
			CloseDate = defense.CloseDate,
			LastChallengeDate = defense.LastChallengeDate,
			Created = defense.Created
		};
	}

	private static siege_ledger_bits ToBits(SiegeLedger entry) => new()
	{
		Id = entry.Id,
		SiegeId = entry.SiegeId,
		Action = (siege_ledger_bits.SiegeAction)entry.Action,
		Context = entry.Context,
		FightSeq = entry.FightSeq,
		Wins = entry.Wins,
		PlayerId = entry.PlayerProfileId,
		PlayerName = entry.PlayerName,
		PlayerDT = entry.PlayerDeck,
		Forfeit = entry.Forfeit,
		Escrow = (siege_ledger_bits.EscrowState)entry.Escrow,
		Bet = (uint)entry.Bet,
		Fee = (uint)entry.Fee,
		Rake = (uint)entry.Rake,
		Created = entry.Created
	};

	private static bool IsOpen(Siege defense) => Siege.OpenStatuses.Contains(defense.Status);

	// The client shows these dates as they come, without a time zone, so they are server-local like the rest of Dingler.
	private static DateTime Now() => DateTime.Now;

	// Count 0 means "all of them", which is what the client asks for today.
	private static IEnumerable<T> Page<T>(IEnumerable<T> items, int offset, int count)
	{
		var page = items.Skip(Math.Max(offset, 0));
		return count > 0 ? page.Take(count) : page;
	}

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

	private sealed record SiegeLogRequest(ulong SiegeId, ulong PlayerId);

	private sealed record SiegeIdRequest(ulong SiegeId);

	// Deck stays raw JSON: the server only checks it and stores it, and passing it through untouched keeps every field.
	private sealed record FightRequest(JsonElement? Deck, string? Message);

	private sealed record CreateRequest(Messaging.Create.Currency Cur, int PotAmount, FightRequest[]? Fights,
		int ChallengeLimit, ulong DebugReckId);

	// Writes dates the way the client's SimpleDateTime converter reads them.
	private sealed class SiegeDateConverter : JsonConverter<DateTime>
	{
		private const string Format = "yyyy-MM-dd HH:mm:ss";

		public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
			DateTime.ParseExact(reader.GetString()!, Format, CultureInfo.InvariantCulture);

		public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
			writer.WriteStringValue(value.ToString(Format, CultureInfo.InvariantCulture));
	}
}
