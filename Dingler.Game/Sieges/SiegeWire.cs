extern alias HexGame;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dingler.Data.Entities.GameData;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Campaign.Siege;
using HexGame::Game.Shared.Domain;
using HexGame::Game.Shared.Network;
using HexGame::Game.Shared.Profile;
using HexGame::Game.Shared.Utils;

namespace Dingler.Game.Sieges;

/// <summary>
/// Siege's rules numbers and everything that turns server data into what the client reads: replies, events, the
/// defense's packed fights and the decks inside them. See notes/siege-protocol.md.
/// </summary>
public static class SiegeWire
{
	public const int FightsPerDefense = 3;

	// Fee rules from the client's rules text: 1000 gold / 50 platinum minimum bounty, attacking costs 40% of the
	// bounty, and of that fee 50% goes to the defender and 37.5% is added to the bounty (the rest is burned).
	public static readonly Messaging.FeeDetail Fees = new(1000, 50, 0.4f, 0.5f, 0.375f);

	// The wait on the assault screen before each game.
	public static readonly TimeSpan Intermission = TimeSpan.FromSeconds(30);

	// The client reads replies with Newtonsoft.Json: public fields, exact field names, and enums as names or numbers.
	// Its Siege dates must be "yyyy-MM-dd HH:mm:ss", and a missing date is left out rather than sent as null.
	// (The game DLL's own Newtonsoft copy can read here but fails to write outside Unity, so we write with System.Text.Json.)
	public static readonly JsonSerializerOptions JsonOptions = new()
	{
		IncludeFields = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Converters = { new JsonStringEnumConverter(), new SiegeDateConverter() }
	};

	// "siege:challenge" tells the attacker where the attack stands: CurState INTERMISSION with the time of the next game
	// (the client's VICTOR handling never shows a result, so the end of an attack is a "siege:saction" instead). Seq is the number of fights already won; KnownChamps shows the champions met so far plus the next one.
	// Written by hand: the client reads it with Newtonsoft into SiegeState, whose constructor arguments it can't fill
	// from our field names, and Source (a UID) is only used to remember the server, so it is left out.
	public static Messaging.Request ChallengeEvent(Siege defense, int seq, DateTime nextGame)
	{
		using var buffer = new MemoryStream();
		using (var writer = new Utf8JsonWriter(buffer))
		{
			writer.WriteStartObject();
			writer.WriteString("RequestType", Messaging.Event_SiegeChallenge);
			writer.WriteStartObject("State");
			// In UTC: with the server's local time the client showed no countdown (it seems to compare against UTC).
			writer.WriteString("nextEval", nextGame.ToUniversalTime().ToString(SiegeDateConverter.Format, CultureInfo.InvariantCulture));
			writer.WritePropertyName("PubInfo");
			JsonSerializer.Serialize(writer, ToBits(defense), JsonOptions);
			writer.WriteNumber("Seq", seq);
			writer.WriteStartArray("KnownChamps");
			foreach (var champion in UnpackFights(defense.FightInfo).Take(seq + 1).Select(f => f.GetProperty("Deck").GetProperty("Champ")))
				champion.WriteTo(writer);
			writer.WriteEndArray();
			writer.WriteNumber("CurState", (int)Messaging.SiegeState.State.INTERMISSION);
			writer.WriteEndObject();
			writer.WriteEndObject();
		}

		return new Messaging.Request { Envelope = buffer.ToArray() };
	}

	// A deck counts if it has a champion and at least one card. The client sends the whole deck (Newtonsoft's view of
	// ProfileDeckTemplate): the champion as {"m_Guid": ...} in "Champ", the cards as [{Temp, Count, ...}] in "CardDesc".
	public static bool IsPlayableDeck(JsonElement? deck)
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

	// The 3 fights back out of siege_bits.FightInfo, each {Deck, Message}.
	public static JsonElement[] UnpackFights(byte[] fightInfo)
	{
		using var document = JsonDocument.Parse(Compressor.Decompress(fightInfo));
		return document.RootElement.EnumerateArray().Select(f => f.Clone()).ToArray();
	}

	// What an attacker pays, split the way the rules text describes: a fee to the defender, a bet added to the bounty,
	// and the rest burned (the rake).
	public static SiegeCost CostOf(int pot) =>
		SiegeCost.For(pot, Fees.ChallengeCostPotPct, Fees.ChallengePotFeePct, Fees.ChallengePotBetPct);

	public static siege_bits ToBits(Siege defense)
	{
		var cost = CostOf(defense.Pot);

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
			ChallengeFee = cost.Fee,
			ChallengeBet = cost.Bet,
			ChallengeRake = cost.Rake,
			Currency = (siege_bits.CurrencyType)defense.Currency,
			CloseDate = defense.CloseDate,
			LastChallengeDate = defense.LastChallengeDate,
			Created = defense.Created
		};
	}

	public static siege_ledger_bits ToBits(SiegeLedger entry) => new()
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

	// The client shows these dates as they come, without a time zone, so they are server-local like the rest of Dingler.
	public static DateTime Now() => DateTime.Now;

	// Writes dates the way the client's SimpleDateTime converter reads them.
	public sealed class SiegeDateConverter : JsonConverter<DateTime>
	{
		public const string Format = "yyyy-MM-dd HH:mm:ss";

		public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
			DateTime.ParseExact(reader.GetString()!, Format, CultureInfo.InvariantCulture);

		public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
			writer.WriteStringValue(value.ToString(Format, CultureInfo.InvariantCulture));
	}

	// "siege:gstart" starts game Seq (0-based, like the challenge event's Seq) of Max: the client opens the battle from
	// GameSession, an EncData-encoded SessionState, and shows the defender's taunt Msg. Written by hand like
	// ChallengeEvent; GameSession goes out as base64, which is how Newtonsoft reads a byte array.
	// "siege:saction" carries one events log row; the client adds it to its log, and the last one decides the results
	// shown after a Siege game (DEFEATED is a victory for the attacker). Source is left out as in the other events.
	public static Messaging.Request ActionEvent(SiegeLedger row)
	{
		using var buffer = new MemoryStream();
		using (var writer = new Utf8JsonWriter(buffer))
		{
			writer.WriteStartObject();
			writer.WriteString("RequestType", Messaging.Event_SiegeAction);
			writer.WritePropertyName("Event");
			JsonSerializer.Serialize(writer, ToBits(row), JsonOptions);
			writer.WriteEndObject();
		}

		return new Messaging.Request { Envelope = buffer.ToArray() };
	}

	public static Messaging.Request GameStartEvent(SessionState session, ulong siegeId, string? taunt, int seq)
	{
		using var buffer = new MemoryStream();
		using (var writer = new Utf8JsonWriter(buffer))
		{
			writer.WriteStartObject();
			writer.WriteString("RequestType", Messaging.Event_SiegeGameStart);
			writer.WriteBase64String("GameSession", EncData.Encode(session));
			writer.WriteNumber("Max", FightsPerDefense);
			writer.WriteNumber("Seq", seq);
			writer.WriteNumber("SiegeId", siegeId);
			writer.WriteString("Msg", taunt ?? "");
			writer.WriteEndObject();
		}

		return new Messaging.Request { Envelope = buffer.ToArray() };
	}

	// A deck as the client sent it (a ProfileDeckTemplate) as the deck_bits a game loads, through the client's own
	// conversion: champion, equipment, cards with their gems, and the AI personality. The client's JSON reader can read
	// it here (only its writer needs Unity).
	public static deck_bits ToDeckBits(JsonElement deck) => ToDeckTemplate(deck).AsFakeDeck();

	/// <summary>A deck as the client sent it, read into the client's own ProfileDeckTemplate.</summary>
	public static ProfileDeckTemplate ToDeckTemplate(JsonElement deck)
	{
		ProfileDeckTemplate? template = null;
		HexGame::Game.Shared.Network.HConnect.Utils.DeserializeJson<ProfileDeckTemplate>(JsonSerializer.SerializeToUtf8Bytes(deck),
			t => template = t);
		return template ?? throw new JsonException("The deck couldn't be read.");
	}
}
