
extern alias HexGame;

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Pipeline;
using Dingler.Server.Systems;
using Dingler.Game.Protocol.Chat;
using Dingler.Game.Protocol.Messages;
using Dingler.Game.Protocol.Messages.Args;
using Dingler.Game.Protocol.Messages.Json;
using Dingler.Game.Protocol.Messages.Metadata;
using Dingler.Game.Protocol.Rooms.Models;
using HexGame::Game.Shared.Campaign.Siege;
using HexGame::Game.Shared.Mail.Messages;
using HexGame::Game.Shared.Network;
using HexGame::Game.Shared.Network.Campaign;
using HexGame::Game.Shared.Network.Escrow;
using HexGame::Game.Shared.Network.GameSession;
using HexGame::Game.Shared.Network.Mail;
using HexGame::Game.Shared.Network.Matchmaking;
using HexGame::Game.Shared.Network.Profile;
using HexGame::Game.Shared.Network.SFS;
using HexGame::Game.Shared.Network.Tournaments;
using HexGame::Game.Shared.Utils;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace Dingler.Game.Protocol.Middleware;

public sealed class EncodeMiddleware : IMiddleware<RequestContext>
{
	private static readonly JsonSerializerOptions JsonSerializerOptions = new()
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		IncludeFields = true,
		Converters =
		{
			new TournamentDescConverter(),
			new RoomUpdateConverter(),
			new WaitingRoomUpdateConverter(),
			new TournamentInfoConverter(),
			new TournamentPlayerInfoConverter(),
			new TournamentGameInfoConverter(),
		}
	};
	private static ReadOnlySpan<byte> Identifier => "~HCP~"u8;

	private static readonly Dictionary<Type, string> IssuerTypeMap = new()
	{
		{typeof(ClientConnection.AuthInfo), IssuerIdentifiers.Session.SESSION},
		{typeof(CreateNewSessionResponse), IssuerIdentifiers.Session.SESSION},
		{typeof(PingResponseArg), IssuerIdentifiers.Session.SESSION},
		{typeof(RawChatRequest), IssuerIdentifiers.Session.SESSION},
		{typeof(RoomData), IssuerIdentifiers.Session.SESSION},
		{typeof(RoomListFrame), IssuerIdentifiers.Session.SESSION},
		{typeof(Mail.GetUnreadMailCount.Request), IssuerIdentifiers.Mail.UNREAD_MAIL_RESPONSE},
		{typeof(ProfileStreamEventArgs), IssuerIdentifiers.Profile.STREAM_PROFILE_INFO},
	};	
	
	private static readonly Dictionary<Type, string> TargetTypeMap = new()
	{
		{typeof(ClientConnection.AuthInfo), TargetIdentifiers.AUTHENTICATION_RESPONSE},
		{typeof(CreateNewSessionResponse), TargetIdentifiers.NEW_SESSION_TARGET},
		{typeof(PingResponseArg), TargetIdentifiers.PONG},
		{typeof(RawChatRequest), "chat"},
		{typeof(RoomData), "chat"},
		{typeof(RoomListFrame), "chat"},
	};

	private static readonly Dictionary<Type, string> InstanceTypeMap = new()
	{

	};
	
	// Events. These are server initiated with no real way to map if there was client request that spawned it
	private static readonly Dictionary<Type, int> EventTypeMap = new(capacity: 74)
	{
		{ typeof(NewMailReceivedEventArgs), 9005 },
		{ typeof(ArenaRefreshEventArgs), 10045 },
		{ typeof(LootUpdateEventArgs), 10046 },
		{ typeof(BuffConversationEventArgs), 10047 },
		{ typeof(AuctionHouseBalanceDeltaUpdateEventArgs), 6017 },
		{ typeof(InventoryItemAddedEventArgs), 6018 },
		{ typeof(CardAddedEventArgs), 6019 },
		{ typeof(SessionResyncEventArgs), 3049 },
		{ typeof(PlayerAddedEventArgs), 3050 },
		{ typeof(PlayerRemovedEventArgs), 3051 },
		{ typeof(GameContinueEventArgs), 3052 },
		{ typeof(GameStartedEventArgs), 3053 },
		{ typeof(GameEndedEventArgs), 3054 },
		{ typeof(SessionSyncEventEventArgs), 3055 },
		{ typeof(ChampionStatsUpdatedEventArgs), 3056 },
		{ typeof(FoundQuickMatchEventArgs), 4025 },
		{ typeof(SendQuickmatchSessionEventArgs), 4026 },
		{ typeof(FoundChallengeMatchEventArgs), 4027 },
		{ typeof(SendChallangeSessionEventArgs), 4028 },
		{ typeof(MarkClientForAntiAddictionEventEventArgs), 2189 },
		{ typeof(AntiAddictionReminderEventEventArgs), 2190 },
		{ typeof(DeckRemovedEventArgs), 2191 },
		{ typeof(FriendRequestReceivedEventArgs), 2192 },
		{ typeof(FriendRequestAcceptedEventArgs), 2193 },
		{ typeof(FriendAddedEventArgs), 2194 },
		{ typeof(FriendRemovedEventArgs), 2195 },
		{ typeof(IgnoredListArrivedEventArgs), 2196 },
		{ typeof(PlayerIgnoredEventArgs), 2197 },
		{ typeof(PlayerUnignoredEventArgs), 2198 },
		{ typeof(PendingFriendRequestsArrivedEventArgs), 2199 },
		{ typeof(FriendRequestRemovedEventArgs), 2200 },
		{ typeof(UserFlagsUpdatedEventArgs), 2201 },
		{ typeof(FriendsListArrivedEventArgs), 2202 },
		{ typeof(FriendComeOnlineEventArgs), 2203 },
		{ typeof(FriendGoesOfflineEventArgs), 2204 },
		{ typeof(CardsAddedEventArgs), 2205 },
		{ typeof(CardRemovedEventArgs), 2206 },
		{ typeof(InventoryUpdatedEventArgs), 2207 },
		{ typeof(FullInventoryRefreshEventArgs), 2208 },
		{ typeof(BalanceUpdateEventArgs), 2209 },
		{ typeof(ProfileStreamEventArgs), 2210 },
		{ typeof(ProfileGenericUpdateEventArgs), 2211 },
		{ typeof(PlayerMessageEventArgs), 2212 },
		{ typeof(MessageOfTheDayEventArgs), 2213 },
		{ typeof(BannedCardListEventArgs), 2214 },
		{ typeof(TrackStatusUpdateEventArgs), 2215 },
		{ typeof(ReceiveNonTournamentPrizeEventArgs), 2216 },
		{ typeof(ReceiveGenericLootRewardsEventArgs), 2217 },
		{ typeof(KickedFromWaitingRoomEventArgs), 25051 },
		{ typeof(TournamentNotificationEventArgs), 25052 },
		{ typeof(TournamentScheduledToBeginEventArgs), 25053 },
		{ typeof(InitTournamentDescriptionsEventArgs), 25054 },
		{ typeof(TournamentDescriptionUpdatedEventArgs), 25055 },
		{ typeof(TournamentCancelledEventArgs), 25056 },
		{ typeof(TournamentGameInvitationEventArgs), 25057 },
		{ typeof(TournamentGameResultsEventArgs), 25058 },
		{ typeof(TournamentInfoEventArgs), 25059 },
		{ typeof(TournamentSessionStartEventArgs), 25060 },
		{ typeof(GotoSideboardingEventArgs), 25061 },
		{ typeof(GotoLobbyEventArgs), 25062 },
		{ typeof(TournamentResultsEventArgs), 25063 },
		{ typeof(TournamentPlayerJoinedEventArgs), 25064 },
		{ typeof(AsynchPlayerUpdatedEventArgs), 25065 },
		{ typeof(TournamentPlayerLeftEventArgs), 25066 },
		{ typeof(TournamentPlayerDisqualifiedEventArgs), 25067 },
		{ typeof(TournamentStartedEventArgs), 25068 },
		{ typeof(DraftStartingEventArgs), 25069 },
		{ typeof(ReceiveDraftPackEventArgs), 25070 },
		{ typeof(ReceiveSelectedCardEventArgs), 25071 },
		{ typeof(DeckConstructionStartedEventArgs), 25072 },
		{ typeof(TournamentCompletedEventArgs), 25073 },
		{ typeof(TournamentByeReceivedEventArgs), 25074 },
		{ typeof(TournamentOpponentOfflineEventArgs), 25075 },
		{ typeof(TournamentReceivePrizeEventArgs), 25076 },
		{ typeof(Messaging.Request), 150000 },   // Siege events (siege:challenge, siege:gstart, siege:saction)
	};

	public async Task InvokeAsync(RequestContext context, PipelineBuilder<RequestContext>.MiddlewareDelegate next,
		CancellationToken token)
	{
		var payloadType = context.ResponseObject!.GetType();
		var header = ResolveHeader(context, payloadType);
		if (!MessageContext.IsRequestExpectingCustomEncoding(header.Issuer!))
		{
			if (context.ResponseObject is RoomData roomData)
			{
				context.RawResponse = SerializeRoomData(roomData, JsonSerializerOptions);
			}
			else
			{
				var json = JsonSerializer.Serialize(context.ResponseObject, JsonSerializerOptions);
				Debug.WriteLine(json);
				context.RawResponse = Encoding.UTF8.GetBytes(json);
			}
		}
		else
		{
			var encoder = context.SessionContext.Encoder;
			var encodedResponse = encoder.Encode(context.ResponseObject);
			var compressedResponse = Compressor.Compress(encodedResponse, Deflater.BEST_COMPRESSION);
			context.TryGetRequestId(out var requestId);
			var dataType = context.TryGetDataType(out var eventType) ? eventType : ResolveDataType(payloadType);
			var wrapper = new DataWrapper()
			{
				Bytes = compressedResponse,
				DataType = dataType,
				RequestId = requestId > 0 ? requestId | 1 : 0,
				Comp = 1
			};
			
			if (wrapper.DataType == 0)
				Debug.WriteLine("break here");
			
			context.RawResponse = encoder.Encode(wrapper);
		}

		header.ServerCount = context.SessionContext.CurrentMessageCount;
		context.SessionContext.CurrentMessageCount++;
		
		context.RawResponse = SerializeResponseForHex(header, context.RawResponse);
		
		await next(context, token);
	}

	private static byte[] SerializeResponseForHex(Header header, byte[] contextRawResponse)
	{
		using var memoryStream = new MemoryStream();
		using var binaryWriter = new BinaryWriter(memoryStream);
		
		binaryWriter.Write(Identifier.ToArray(), 0, Identifier.Length);
		
		var headerBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(header, JsonSerializerOptions));
		
		var fullMessageLength = headerBytes.Length + contextRawResponse.Length + 8;
		binaryWriter.Write(BitConverter.GetBytes(fullMessageLength).Reverse().ToArray(), 0, 4);
		binaryWriter.Write(BitConverter.GetBytes(headerBytes.Length).Reverse().ToArray(), 0, 4);
		binaryWriter.Write(headerBytes, 0, headerBytes.Length);
		binaryWriter.Write(BitConverter.GetBytes(contextRawResponse.Length).Reverse().ToArray(), 0, 4);
		binaryWriter.Write(contextRawResponse, 0, contextRawResponse.Length);
		
		return memoryStream.ToArray();
	}

	private static Header ResolveHeader(RequestContext context, Type responseType, bool isDataWrapper = false)
	{
		return new Header()
		{
			Issuer = TryResolveIssuer(responseType, out var issuerString) ?  issuerString : "123.123.123.123.123.123",
			Target = TryResolveTarget(responseType, out var targetString) ?  targetString : null,
			Instance = TryResolveInstance(responseType, out var instanceString) ?  instanceString : null,
			ClientCount = 0,
			SessionId = context.SessionContext.SessionId.ToString(),
			RequestId = context.TryGetRequestId(out var requestId) ? requestId | 1 : 0,
		};
	}

	private static int ResolveDataType(Type type)
	{
		return EventTypeMap.TryGetValue(type, out var code)
			? code
			: throw new NotImplementedException($"Could not find type {type.FullName}");
	}
	
	private static bool TryResolveTarget(Type type, [MaybeNullWhen(false)] out string targetType)
	{
		if (TargetTypeMap.TryGetValue(type, out targetType))
			return true;

		if (type.IsGenericType && TargetTypeMap.TryGetValue(type.GetGenericTypeDefinition(), out targetType))
			return true;
		
		targetType = null;
		return false;
	}
	
	private static bool TryResolveIssuer(Type type, [MaybeNullWhen(false)] out string isserString)
	{
		if (IssuerTypeMap.TryGetValue(type, out isserString))
			return true;

		if (type.IsGenericType && IssuerTypeMap.TryGetValue(type.GetGenericTypeDefinition(), out isserString))
			return true;
		
		isserString = null;
		return false;
	}
	
	private static bool TryResolveInstance(Type type, [MaybeNullWhen(false)] out string instanceString)
	{
		if (InstanceTypeMap.TryGetValue(type, out instanceString))
			return true;

		if (type.IsGenericType && InstanceTypeMap.TryGetValue(type.GetGenericTypeDefinition(), out instanceString))
			return true;
		
		instanceString = null;
		return false;
	}
	
	private static byte[] SerializeRoomData(RoomData value, JsonSerializerOptions options)
	{
		var json = JsonSerializer.Serialize(value.Updates, options);
		var compressedJson = Compressor.Compress(Encoding.UTF8.GetBytes(json), Deflater.BEST_COMPRESSION);

		using var headerBuffer = new MemoryStream();
		using (var headerWriter = new Utf8JsonWriter(headerBuffer))
		{
			headerWriter.WriteStartObject();
			headerWriter.WriteString("action", "rdata");
			headerWriter.WriteString("room", value.Room);
			headerWriter.WriteString("rflg", value.RoomFlags);
			headerWriter.WriteString("flg", value.Flags);
			headerWriter.WriteString("user", value.Sender);
			headerWriter.WriteNumber("sz", compressedJson.Length);
			headerWriter.WriteEndObject();
		}

		byte[] headerBytes = headerBuffer.ToArray();
		byte[] finalBytes = new byte[headerBytes.Length + compressedJson.Length];
		Buffer.BlockCopy(headerBytes, 0, finalBytes, 0, headerBytes.Length);
		Buffer.BlockCopy(compressedJson, 0, finalBytes, headerBytes.Length, compressedJson.Length);
		return finalBytes;
	}

}