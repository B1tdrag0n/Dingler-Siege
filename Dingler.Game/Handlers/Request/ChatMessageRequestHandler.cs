using Dingler.Data.Enums;
using Dingler.Game.DeckImport;
using Dingler.Game.Protocol.Chat;
using Dingler.Game.Wallet;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;

namespace Dingler.Game.Handlers.Request;

[Authenticated]
public class ChatMessageRequestHandler : IAsyncRequestHandler<ChatMessageRequest>
{
	private const string ImportCommand = "/importdeck";

	// TEST ONLY (Milestone 4): "/givemoney <amount> [gold|platinum]" adds money to the sender's wallet so Siege bounties
	// can be tested. Remove before merging back into Dingler (Milestone 8).
	private const string GiveMoneyCommand = "/givemoney";

	private readonly ChatManager _chatManager;
	private readonly DeckImportService _deckImport;
	private readonly WalletService _wallet;

	public ChatMessageRequestHandler(ChatManager chatManager, DeckImportService deckImport, WalletService wallet)
	{
		_chatManager = chatManager;
		_deckImport = deckImport;
		_wallet = wallet;
	}
	
	public async Task HandleRequestAsync(SessionContext context, ChatMessageRequest request, CancellationToken token)
	{
		// Fork: "/importdeck <Hex Codex deck link>" imports a deck for the sender (DeckImportService). It is never
		// broadcast; the answer goes to the sender only, as a chat line in the same room.
		var raw = request.RawChatRequest;
		var message = raw.Message.TrimStart();
		if (message.StartsWith(ImportCommand, StringComparison.OrdinalIgnoreCase))
		{
			var result = await _deckImport.ImportLinkAsync(context, message[ImportCommand.Length..]);
			context.TrySendMessageToClient(new RawChatRequest { Action = "rchat", Room = raw.Room, User = "Deck import", Message = result.Message });
			return;
		}

		if (message.StartsWith(GiveMoneyCommand, StringComparison.OrdinalIgnoreCase))
		{
			var answer = GiveMoney(context, message[GiveMoneyCommand.Length..]);
			context.TrySendMessageToClient(new RawChatRequest { Action = "rchat", Room = raw.Room, User = "Wallet", Message = answer });
			return;
		}

		await _chatManager.SendMessageAsync(raw);
	}

	private string GiveMoney(SessionContext context, string arguments)
	{
		const string usage = "Usage: /givemoney <amount> [gold|platinum], e.g. /givemoney 5000 gold";

		var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], out var amount) || amount is <= 0 or > 1_000_000)
			return usage;

		SiegeCurrency currency;
		switch (parts.Length == 2 ? parts[1].ToLowerInvariant() : "gold")
		{
			case "gold" or "g":
				currency = SiegeCurrency.Gold;
				break;
			case "platinum" or "plat" or "p":
				currency = SiegeCurrency.Platinum;
				break;
			default:
				return usage;
		}

		var balance = _wallet.Add(context.ProfileId, currency, amount);
		_wallet.SendBalance(context);
		return $"Added {amount} {currency.ToString().ToLowerInvariant()}. You now have {balance.Gold} gold and {balance.Platinum} platinum.";
	}
}
