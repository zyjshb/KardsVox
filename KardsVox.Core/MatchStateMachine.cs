using System;

namespace KardsVox.Core;

internal sealed class MatchStateMachine
{
	private readonly int boardFrames;

	private readonly int preMatchFrames;

	private readonly int outsideArmFrames;

	private int boardStreak;

	private int preMatchStreak;

	private int outsideStreak;

	private bool armed;

	public MatchState State { get; private set; }

	public MatchStateMachine(DetectionSettings settings)
	{
		boardFrames = settings.BoardConfirmFrames;
		preMatchFrames = settings.LobbyConfirmFrames;
		outsideArmFrames = settings.OutsideArmFrames;
	}

	public bool Observe(bool board, bool preMatch, DateTimeOffset now)
	{
		switch (State)
		{
		case MatchState.OutsideMatch:
			if (!board)
			{
				if (++outsideStreak >= outsideArmFrames)
				{
					armed = true;
				}
				return false;
			}
			outsideStreak = 0;
			if (!armed)
			{
				State = MatchState.InMatch;
				return false;
			}
			State = MatchState.EnteringMatch;
			boardStreak = 1;
			return false;
		case MatchState.EnteringMatch:
			if (!board)
			{
				State = MatchState.OutsideMatch;
				boardStreak = 0;
				outsideStreak = 1;
				return false;
			}
			if (++boardStreak >= boardFrames)
			{
				State = MatchState.InMatch;
				preMatchStreak = 0;
				armed = false;
				return true;
			}
			return false;
		case MatchState.InMatch:
			if (preMatch && !board)
			{
				if (++preMatchStreak >= preMatchFrames)
				{
					State = MatchState.MatchEnded;
					armed = true;
				}
			}
			else
			{
				preMatchStreak = 0;
			}
			return false;
		case MatchState.MatchEnded:
			if (board)
			{
				State = MatchState.EnteringMatch;
				boardStreak = 1;
				return false;
			}
			State = MatchState.OutsideMatch;
			boardStreak = 0;
			preMatchStreak = 0;
			outsideStreak = 0;
			armed = true;
			return false;
		default:
			return false;
		}
	}

	public void WindowClosed()
	{
		State = MatchState.OutsideMatch;
		boardStreak = (preMatchStreak = (outsideStreak = 0));
		armed = false;
	}
}
