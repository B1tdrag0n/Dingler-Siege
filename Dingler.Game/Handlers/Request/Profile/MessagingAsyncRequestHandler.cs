extern alias HexGame;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dingler.Data.Entities.GameData;
using Dingler.Data.Enums;
using Dingler.Data.Repositories;
using Dingler.Game.Sieges;
using static Dingler.Game.Sieges.SiegeWire;
using Dingler.Game.Wallet;
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
	// Not built yet: testing your own defense and practice games. Lock is only an admin console command in the client.
	// These get a clear refusal rather than silence.
	private static readonly HashSet<string> NotAvailableRequests =
	[
		Messaging.ReqType_TestListing, Messaging.ReqType_PracticeGame, Messaging.ReqType_Lock
	];

	private readonly SiegeRepository _sieges;
	private readonly WalletService _wallet;
	private readonly SiegeAttacks _attacks;
	private readonly SiegePayments _payments;
	private readonly SiegeBattleService _battles;
	private readonly ILogger<MessagingAsyncRequestHandler>? _logger;

	public MessagingAsyncRequestHandler(SiegeRepository sieges, WalletService wallet, SiegeAttacks attacks,
		SiegePayments payments, SiegeBattleService battles, ILogger<MessagingAsyncRequestHandler>? logger = null)
	{
		_sieges = sieges;
		_wallet = wallet;
		_attacks = attacks;
		_payments = payments;
		_battles = battles;
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
				Messaging.ReqType_ChallengeListing => await Challenge(context, Read<ChallengeRequest>(json)),
				Messaging.ReqType_Forfeit => await Forfeit(context, Read<SiegeIdRequest>(json)),
				_ when requestType is not null && NotAvailableRequests.Contains(requestType) =>
					Refuse("That part of Siege isn't available on this server yet."),
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

		// The bounty comes out of the player's wallet up front, and goes back if the defense can't be stored. The defense
		// goes straight to ACTIVE.
		if (!_wallet.TrySpend(context.ProfileId, (SiegeCurrency)currency, request.PotAmount))
			return Refuse($"You don't have {request.PotAmount} {currency.ToString().ToLowerInvariant()} for this bounty.");

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
		{
			_wallet.Add(context.ProfileId, (SiegeCurrency)currency, request.PotAmount);
			return Refuse("You already have an active defense. Withdraw it before committing a new one.");
		}


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

		if (_attacks.TryGetBySiege(defense.Id, out _))
			return Refuse("Your defense is under attack right now. You can withdraw it once the attack is over.");

		var closed = await _sieges.CloseAsync(defense.Id, Now());
		if (closed is null)
			return Refuse("That defense is already closed.");

		// Withdrawing hands the bounty back to the defender.
		_wallet.Add(context.ProfileId, closed.Currency, closed.Pot);

		_logger?.LogInformation("Siege: {user} withdrew defense {id} and got back {pot} {currency}", context.UserName,
			closed.Id, closed.Pot, closed.Currency);
		return new Messaging.Remove.Response { Updated = ToBits(closed) };
	}

	// Begin Siege. The attacker pays up front (Fee to the defender, Bet onto the bounty, the rest burned), the defense goes
	// INGAME so nobody else can attack it, and the CHALLENGE row is logged. After the reply, the "siege:challenge" event
	// moves the attacker to the assault screen with the countdown to the first game.
	private async Task<object> Challenge(SessionContext context, ChallengeRequest request)
	{
		var defense = await _sieges.GetAsync(request.SiegeId);
		if (defense is null || defense.Status != SiegeStatus.Active || _attacks.TryGetBySiege(request.SiegeId, out _))
			return Refuse("That defense isn't open to attack right now.");
		if (defense.PlayerProfileId == context.ProfileId)
			return Refuse("You can't attack your own defense.");
		if (_attacks.IsAttacking(context.ProfileId))
			return Refuse("You're already attacking another defense.");
		if (!IsPlayableDeck(request.Deck))
			return Refuse("Pick a complete deck, with a champion and cards, to attack with.");

		var cost = CostOf(defense.Pot);
		var currencyName = defense.Currency.ToString().ToLowerInvariant();
		if (!_payments.TryChargeAttack(context.ProfileId, defense.PlayerProfileId, defense.Currency, cost))
			return Refuse($"Attacking this defense costs {cost.Total} {currencyName}, and you don't have enough.");

		var attack = new SiegeAttacks.Attack
		{
			SiegeId = defense.Id,
			AttackerId = context.ProfileId,
			AttackerName = context.UserName ?? "",
			Deck = request.Deck!.Value.Clone(),
			Fights = UnpackFights(defense.FightInfo),
			Session = context
		};
		var started = _attacks.TryAdd(attack)
			? await _sieges.TryStartAttackAsync(defense.Id, cost.Bet, new SiegeLedger
			{
				Action = SiegeAction.Challenge,
				PlayerProfileId = context.ProfileId,
				PlayerName = attack.AttackerName,
				PlayerDeck = Compressor.Compress(JsonSerializer.SerializeToUtf8Bytes(attack.Deck)),
				Escrow = SiegeEscrow.Funded,
				Bet = cost.Bet,
				Fee = cost.Fee,
				Rake = cost.Rake,
				Created = Now()
			})
			: null;
		if (started is null)
		{
			// Someone else got there first.
			_attacks.Remove(attack);
			_payments.RefundAttack(context.ProfileId, defense.PlayerProfileId, defense.Currency, cost);
			return Refuse("That defense isn't open to attack right now.");
		}

		_logger?.LogInformation("Siege: {user} attacks {defender}'s defense {id} for {cost} {currency} (bounty now {pot})",
			context.UserName, started.PlayerName, started.Id, cost.Total, currencyName, started.Pot);
		context.Disconnected += () => _battles.OnAttackerDisconnected(attack);

		// The event must reach the client after this reply, so it goes out a moment later. Fight 1 follows the countdown.
		var firstGame = Now() + Intermission;
		var challengeEvent = ChallengeEvent(started, attack.Seq, firstGame);
		_ = Task.Run(async () =>
		{
			await Task.Delay(TimeSpan.FromMilliseconds(500));
			if (!context.TrySendMessageToClient(challengeEvent))
				_logger?.LogWarning("Siege: couldn't send the assault screen to {user}", attack.AttackerName);
		});
		_battles.ScheduleNextGame(attack, firstGame);

		return new Messaging.Challenge.Response { Status = "OK" };
	}

	// Abandon an attack. The defense counts it as defended and goes back to ACTIVE; the attacker's payment isn't returned.
	private async Task<object> Forfeit(SessionContext context, SiegeIdRequest request)
	{
		if (!_attacks.TryGetBySiege(request.SiegeId, out var attack))
			return Refuse("There's no attack on that defense to forfeit.");
		if (attack.AttackerId != context.ProfileId)
			return Refuse("That isn't your attack.");

		var ended = await _battles.ForfeitAsync(_sieges, attack);
		if (ended is null)
			return Refuse("There's no attack on that defense to forfeit.");

		_logger?.LogInformation("Siege: {user} forfeited the attack on {defender}'s defense {id}", context.UserName,
			ended.PlayerName, ended.Id);
		return new Messaging.Forfeit.Response { Status = "OK" };
	}

	private static ErrorArgs Refuse(string message) => new() { Error = 1, ErrorMessage = message };

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

	private static bool IsOpen(Siege defense) => Siege.OpenStatuses.Contains(defense.Status);

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

	private sealed record ChallengeRequest(ulong SiegeId, JsonElement? Deck, ulong DebugReckId);

	private sealed record CreateRequest(Messaging.Create.Currency Cur, int PotAmount, FightRequest[]? Fights,
		int ChallengeLimit, ulong DebugReckId);

}
