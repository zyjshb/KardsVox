using System;

namespace KardsVox.Core;

internal static class StateMachineSelfTest
{
	public static void Run()
	{
		DetectionSettings settings = new DetectionSettings();
		DateTimeOffset now = DateTimeOffset.UtcNow;
		MatchStateMachine machine = new MatchStateMachine(settings);
		Check(!Observe(board: true, 0.0) && machine.State == MatchState.InMatch);
		Check(!Observe(board: false, 0.4));
		Check(!Observe(board: true, 0.8) && machine.State == MatchState.InMatch);
		for (int i = 0; i < 20; i++)
		{
			Check(!Observe(board: false, 1.0 + (double)i * 0.4));
		}
		Check(machine.State == MatchState.InMatch);
		Check(!Observe(board: true, 9.1) && machine.State == MatchState.InMatch);
		for (int j = 0; j < settings.LobbyConfirmFrames; j++)
		{
			Check(!Observe(board: false, 10.0 + (double)j * 0.4, preMatch: true));
		}
		Check(machine.State == MatchState.MatchEnded);
		Check(!Observe(board: true, 11.2) && machine.State == MatchState.EnteringMatch);
		Check(!Observe(board: true, 11.6));
		Check(Observe(board: true, 12.0) && machine.State == MatchState.InMatch);
		for (int k = 0; k < 10; k++)
		{
			Check(!Observe(board: true, 12.4 + (double)k * 0.4));
		}
		for (int l = 0; l < settings.LobbyConfirmFrames; l++)
		{
			Check(!Observe(board: false, 20.0 + (double)l * 0.4, preMatch: true));
		}
		Check(machine.State == MatchState.MatchEnded);
		Check(!Observe(board: true, 21.2));
		Check(!Observe(board: true, 21.6));
		Check(Observe(board: true, 22.0));
		machine.WindowClosed();
		Check(machine.State == MatchState.OutsideMatch);
		for (int m = 0; m < settings.OutsideArmFrames; m++)
		{
			Check(!Observe(board: false, 23.0 + (double)m * 0.4));
		}
		Check(!Observe(board: true, 26.0) && machine.State == MatchState.EnteringMatch);
		Check(!Observe(board: true, 26.4));
		Check(Observe(board: true, 26.8) && machine.State == MatchState.InMatch);
		Console.WriteLine("State machine self-test passed.");
		bool Observe(bool board, double seconds, bool preMatch = false)
		{
			return machine.Observe(board, preMatch, now.AddSeconds(seconds));
		}
	}

	private static void Check(bool condition)
	{
		if (!condition)
		{
			throw new InvalidOperationException("State machine self-test failed.");
		}
	}
}
