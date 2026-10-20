namespace KardsVox.Core;

internal static class StateMachineSelfTest
{
    public static void Run()
    {
        var settings = new DetectionSettings();
        var now = DateTimeOffset.UtcNow;
        var machine = new MatchStateMachine(settings);

        bool Observe(bool board, double seconds, bool preMatch = false)
            => machine.Observe(board, preMatch, now.AddSeconds(seconds));

        // Starting in an existing match does not announce it.
        Check(!Observe(true, 0) && machine.State == MatchState.InMatch);

        // Board animations and menus are not evidence of a new match.
        Check(!Observe(false, .4));
        Check(!Observe(true, .8) && machine.State == MatchState.InMatch);
        for (int i = 0; i < 20; i++) Check(!Observe(false, 1 + i * .4));
        Check(machine.State == MatchState.InMatch);
        Check(!Observe(true, 9.1) && machine.State == MatchState.InMatch);

        // A confirmed selection/matchmaking screen unlocks the next match.
        for (int i = 0; i < settings.LobbyConfirmFrames; i++)
            Check(!Observe(false, 10 + i * .4, preMatch: true));
        Check(machine.State == MatchState.MatchEnded);
        Check(!Observe(true, 11.2) && machine.State == MatchState.EnteringMatch);
        Check(!Observe(true, 11.6));
        Check(Observe(true, 12) && machine.State == MatchState.InMatch);
        for (int i = 0; i < 10; i++) Check(!Observe(true, 12.4 + i * .4));

        // The next selection screen can precede the board immediately.
        for (int i = 0; i < settings.LobbyConfirmFrames; i++)
            Check(!Observe(false, 20 + i * .4, preMatch: true));
        Check(machine.State == MatchState.MatchEnded);
        Check(!Observe(true, 21.2));
        Check(!Observe(true, 21.6));
        Check(Observe(true, 22));

        machine.WindowClosed();
        Check(machine.State == MatchState.OutsideMatch);
        for (int i = 0; i < settings.OutsideArmFrames; i++)
            Check(!Observe(false, 23 + i * .4));
        Check(!Observe(true, 26) && machine.State == MatchState.EnteringMatch);
        Check(!Observe(true, 26.4));
        Check(Observe(true, 26.8) && machine.State == MatchState.InMatch);
        Console.WriteLine("State machine self-test passed.");
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("State machine self-test failed.");
    }
}
