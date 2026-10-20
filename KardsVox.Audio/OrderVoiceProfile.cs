using System;
using System.Runtime.CompilerServices;

namespace KardsVox.Audio;

internal sealed record OrderVoiceProfile(string Name, string Event, string Telegraph, string[] Aliases)
{
	public static readonly OrderVoiceProfile Generic = new OrderVoiceProfile("作战命令", "ORDER_ANY", ".. .. ..", Array.Empty<string>());

	[CompilerGenerated]
	private OrderVoiceProfile(OrderVoiceProfile original)
	{
		Name = original.Name;
		Event = original.Event;
		Telegraph = original.Telegraph;
		Aliases = original.Aliases;
	}
}
