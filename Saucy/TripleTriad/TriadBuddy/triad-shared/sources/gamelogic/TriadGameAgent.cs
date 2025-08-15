﻿using System;
using System.Threading;
using System.Threading.Tasks;

namespace FFTriadBuddy;

public abstract class TriadGameAgent
{
    [Flags]
    public enum DebugFlags
    {
        None = 0,
        AgentInitialize = 0x1,
        ShowMoveResult = 0x2,
        ShowMoveStart = 0x4,
        ShowMoveDetails = 0x8,
        ShowMoveDetailsRng = 0x10,
    }
    public DebugFlags debugFlags;
    public string agentName = "??";

    public virtual void Initialize(TriadGameSolver solver, int sessionSeed) { }
    public virtual bool IsInitialized() { return true; }
    public virtual float GetProgress() { return 0.0f; }
    public virtual void OnSimulationStart() { }

    public abstract bool FindNextMove(TriadGameSolver solver, TriadGameSimulationState gameState, out int cardIdx, out int boardPos, out SolverResult solverResult);
}

/// <summary>
/// Random pick from all possible actions 
/// </summary>
public class TriadGameAgentRandom : TriadGameAgent
{
    public static bool UseEqualDistribution = false;
    private Random randGen;

    public TriadGameAgentRandom() { }
    public TriadGameAgentRandom(TriadGameSolver solver, int sessionSeed)
    {
        Initialize(solver, sessionSeed);
    }

    public override void Initialize(TriadGameSolver solver, int sessionSeed)
    {
        randGen = new Random(sessionSeed);
        agentName = "Random";
    }

    public override bool IsInitialized()
    {
        return randGen != null;
    }

    public override bool FindNextMove(TriadGameSolver solver, TriadGameSimulationState gameState, out int cardIdx, out int boardPos, out SolverResult solverResult)
    {
        cardIdx = -1;
        boardPos = -1;
        solverResult = SolverResult.Zero;

        if (!IsInitialized())
        {
            return false;
        }

        if (UseEqualDistribution)
        {
            // proper solution, but ends up lowering initial win chance by A LOT

            solver.FindAvailableActions(gameState, out var availBoardMask, out var numAvailBoard, out var availCardsMask, out var numAvailCards);
            if (numAvailCards > 0 && numAvailBoard > 0)
            {
                cardIdx = PickBitmaskIndex(availCardsMask, numAvailCards);
                boardPos = PickBitmaskIndex(availBoardMask, numAvailBoard);
            }
        }
        else
        {
            // OLD IMPLEMENTATION for comparison
            // doesn't guarantee equal distribution = opponent simulation is biased => reported win chance is too high
            // stays for now until i can make CarloScored usable
            //
            const int boardPosMax = TriadGameSimulationState.boardSizeSq;
            if (gameState.numCardsPlaced < TriadGameSimulationState.boardSizeSq)
            {
                var testPos = randGen.Next(boardPosMax);
                for (var passIdx = 0; passIdx < boardPosMax; passIdx++)
                {
                    testPos = (testPos + 1) % boardPosMax;
                    if (gameState.board[testPos] == null)
                    {
                        boardPos = testPos;
                        break;
                    }
                }
            }

            cardIdx = -1;
            var useDeck = (gameState.state == ETriadGameState.InProgressBlue) ? gameState.deckBlue : gameState.deckRed;
            if (useDeck.availableCardMask > 0)
            {
                var testIdx = randGen.Next(TriadDeckInstance.maxAvailableCards);
                for (var passIdx = 0; passIdx < TriadDeckInstance.maxAvailableCards; passIdx++)
                {
                    testIdx = (testIdx + 1) % TriadDeckInstance.maxAvailableCards;
                    if ((useDeck.availableCardMask & (1 << testIdx)) != 0)
                    {
                        cardIdx = testIdx;
                        break;
                    }
                }
            }
        }

        return (boardPos >= 0) && (cardIdx >= 0);
    }

    protected int PickBitmaskIndex(int mask, int numSet)
    {
        var stepIdx = randGen.Next(numSet);
        return PickRandomBitFromMask(mask, stepIdx);
    }

    public static int PickRandomBitFromMask(int mask, int randStep)
    {
        var bitIdx = 0;
        var testMask = 1 << bitIdx;
        while (testMask <= mask)
        {
            if ((testMask & mask) != 0)
            {
                randStep--;
                if (randStep < 0)
                {
                    return bitIdx;
                }
            }

            bitIdx++;
            testMask <<= 1;
        }

        return -1;
    }
}

/// <summary>
/// Base class for agents recursively exploring action graph
/// </summary>
public abstract class TriadGameAgentGraphExplorer : TriadGameAgent
{
    protected float currentProgress = 0;
    protected int sessionSeed = 0;
    private Random failsafeRandStream = null;

    public override float GetProgress()
    {
        return currentProgress;
    }

    public override void Initialize(TriadGameSolver solver, int sessionSeed)
    {
        this.sessionSeed = sessionSeed;
    }

    public override bool FindNextMove(TriadGameSolver solver, TriadGameSimulationState gameState, out int cardIdx, out int boardPos, out SolverResult solverResult)
    {
        cardIdx = -1;
        boardPos = -1;

        var isFinished = IsFinished(gameState, out solverResult);
        if (!isFinished && IsInitialized())
        {
            _ = SearchActionSpace(solver, gameState, 0, out cardIdx, out boardPos, out solverResult);
        }

        return (cardIdx >= 0) && (boardPos >= 0);
    }

    protected bool IsFinished(TriadGameSimulationState gameState, out SolverResult gameResult)
    {
        // end game conditions, owner always fixed as blue
        switch (gameState.state)
        {
            case ETriadGameState.BlueWins:
                gameResult = new SolverResult(1, 0, 1);
                return true;

            case ETriadGameState.BlueDraw:
                gameResult = new SolverResult(0, 1, 1);
                return true;

            case ETriadGameState.BlueLost:
                gameResult = new SolverResult(0, 0, 1);
                return true;

            default: break;
        }

        gameResult = SolverResult.Zero;
        return false;
    }

    protected virtual SolverResult SearchActionSpace(TriadGameSolver solver, TriadGameSimulationState gameState, int searchLevel, out int bestCardIdx, out int bestBoardPos, out SolverResult bestActionResult)
    {
        // don't check finish condition at start! 
        // this is done before caling this function (from FindNextMove / recursive), so it doesn't have to be duplicated in every derrived class

        bestCardIdx = -1;
        bestBoardPos = -1;
        bestActionResult = SolverResult.Zero;

        // game in progress, explore actions
        var isRootLevel = searchLevel == 0;
        if (isRootLevel)
        {
            currentProgress = 0.0f;
        }

        float numWinsTotal = 0;
        float numDrawsTotal = 0;
        long numGamesTotal = 0;

        solver.FindAvailableActions(gameState, out var availBoardMask, out var numAvailBoard, out var availCardsMask, out var numAvailCards);
        if (numAvailCards > 0 && numAvailBoard > 0)
        {
            var turnOwner = (gameState.state == ETriadGameState.InProgressBlue) ? ETriadCardOwner.Blue : ETriadCardOwner.Red;
            var cardProgressCounter = 0;
            var hasValidPlacements = false;

            for (var cardIdx = 0; cardIdx < TriadDeckInstance.maxAvailableCards; cardIdx++)
            {
                var cardNotAvailable = (availCardsMask & (1 << cardIdx)) == 0;
                if (cardNotAvailable)
                {
                    continue;
                }

                if (isRootLevel)
                {
                    currentProgress = 1.0f * cardProgressCounter / numAvailCards;
                    cardProgressCounter++;
                }

                for (var boardIdx = 0; boardIdx < gameState.board.Length; boardIdx++)
                {
                    var boardNotAvailable = (availBoardMask & (1 << boardIdx)) == 0;
                    if (boardNotAvailable)
                    {
                        continue;
                    }

                    var gameStateCopy = new TriadGameSimulationState(gameState);
                    var useDeck = (gameStateCopy.state == ETriadGameState.InProgressBlue) ? gameStateCopy.deckBlue : gameStateCopy.deckRed;

                    var isPlaced = solver.simulation.PlaceCard(gameStateCopy, cardIdx, useDeck, turnOwner, boardIdx);
                    if (isPlaced)
                    {
                        // check if finished before going deeper
                        var isFinished = IsFinished(gameStateCopy, out var branchResult);
                        if (!isFinished)
                        {
                            gameStateCopy.forcedCardIdx = -1;
                            branchResult = SearchActionSpace(solver, gameStateCopy, searchLevel + 1, out _, out _, out _);
                        }

                        if (branchResult.IsBetterThan(bestActionResult) || !hasValidPlacements)
                        {
                            bestActionResult = branchResult;
                            bestCardIdx = cardIdx;
                            bestBoardPos = boardIdx;
                        }

                        numWinsTotal += branchResult.numWins;
                        numDrawsTotal += branchResult.numDraws;
                        numGamesTotal += branchResult.numGames;
                        hasValidPlacements = true;
                    }
                }
            }

            if (!hasValidPlacements)
            {
                // failsafe in case simulation runs into any issues
                failsafeRandStream ??= new Random(sessionSeed);

                bestCardIdx = TriadGameAgentRandom.PickRandomBitFromMask(availCardsMask, failsafeRandStream.Next(numAvailCards));
                bestBoardPos = TriadGameAgentRandom.PickRandomBitFromMask(availBoardMask, failsafeRandStream.Next(numAvailBoard));
            }
        }

        // what to do with results depend on current move owner:
        //   Agent's player (search levels: 0, 2, 4, ...)
        //   - result of processing this level is MAX(result branch)
        //
        //   opponent player (search levels: 1, 3, ...)
        //   - min/max says MIN, but let's go with AVG instead to be more optimistic
        //   - result of processing this level is AVG, use total counters to create chance data

        var isOwnerTurn = (searchLevel % 2) == 0;
        return isOwnerTurn ? bestActionResult : new SolverResult(numWinsTotal, numDrawsTotal, numGamesTotal);
    }
}

/// <summary>
/// Single level MCTS, each available action spins 2000 random games and best one is selected 
/// </summary>
public class TriadGameAgentDerpyCarlo : TriadGameAgentGraphExplorer
{
    protected int numWorkers = 2000;
    protected TriadGameAgentRandom[] workerAgents;

    public override void Initialize(TriadGameSolver solver, int sessionSeed)
    {
        base.Initialize(solver, sessionSeed);
        agentName = "DerpyCarlo";

        // initialize all random streams just once, it's enough for seeing and having unique stream for each worker
        workerAgents = new TriadGameAgentRandom[numWorkers];
        for (var idx = 0; idx < numWorkers; idx++)
        {
            workerAgents[idx] = new TriadGameAgentRandom(solver, sessionSeed + idx);
        }
    }

    public override bool IsInitialized()
    {
        return workerAgents != null;
    }

    protected override SolverResult SearchActionSpace(TriadGameSolver solver, TriadGameSimulationState gameState, int searchLevel, out int bestCardIdx, out int bestBoardPos, out SolverResult bestActionResult)
    {
        var runWorkers = CanRunRandomExploration(solver, gameState, searchLevel);
        if (runWorkers)
        {
            bestCardIdx = -1;
            bestBoardPos = -1;
            bestActionResult = FindWinningProbability(solver, gameState);

            return bestActionResult;
        }

        var result = base.SearchActionSpace(solver, gameState, searchLevel, out bestCardIdx, out bestBoardPos, out bestActionResult);

        return result;
    }

    protected virtual bool CanRunRandomExploration(TriadGameSolver solver, TriadGameSimulationState gameState, int searchLevel)
    {
        return searchLevel > 0;
    }

    protected virtual SolverResult FindWinningProbability(TriadGameSolver solver, TriadGameSimulationState gameState)
    {
        var numWinningWorkers = 0;
        var numDrawingWorkers = 0;

        _ = Parallel.For(0, numWorkers, workerIdx =>
        //for (int workerIdx = 0; workerIdx < solverWorkers; workerIdx++)
        {
            var gameStateCopy = new TriadGameSimulationState(gameState);
            var agent = workerAgents[workerIdx];

            solver.RunSimulation(gameStateCopy, agent, agent);

            if (gameStateCopy.state == ETriadGameState.BlueWins)
            {
                _ = Interlocked.Add(ref numWinningWorkers, 1);
            }
            else if (gameStateCopy.state == ETriadGameState.BlueDraw)
            {
                _ = Interlocked.Add(ref numDrawingWorkers, 1);
            }
        });

        // return normalized score so it can be compared 
        return new SolverResult(1.0f * numWinningWorkers / numWorkers, 1.0f * numDrawingWorkers / numWorkers, 1);
    }
}

/// <summary>
/// Switches between derpy MCTS and full exploration depending on size of game space 
/// </summary>
public class TriadGameAgentCarloTheExplorer : TriadGameAgentDerpyCarlo
{
    // 10k seems to be sweet spot
    // - 1k: similar time, lower accuracy
    // - 100k: 8x longer, similar accuracy
    public const long MaxStatesToExplore = 10 * 1000;

    private int minPlacedToExplore = 10;
    private int minPlacedToExploreWithForced = 10;

    public override void Initialize(TriadGameSolver solver, int sessionSeed)
    {
        base.Initialize(solver, sessionSeed);
        agentName = "CarloTheExplorer";

        // cache number of possible states depending on cards placed
        // 0: (5 * 9) * (5 * 8) * (4 * 7) * (4 * 6) * ...                   = (5 * 5 * 4 * 4 * 3 * 3 * 2 * 2 * 1) * 9! = (5! * 5!) * 9!
        // 1: (5 * 8) * (4 * 7) * (4 * 6) * ...                             = (5 * 4 * 4 * 3 * 3 * 2 * 2 * 1) * 8!     = (4! * 5!) * 8!
        // ...
        // 6: (2 * 3) * (1 * 2) * (1 * 1)
        // 7: (1 * 2) * (1 * 1)
        // 8: (1 * 1)
        // 9: 0
        //
        // num states = num board positions * num cards, 
        // - board(num placed) => x == 0 ? 0 : x!
        // - card(num placed) => forced ? 1 : ((x + 2) / 2)! * ((x + 1) / 2)!

        long numStatesForced = 1;
        long numStates = 1;

        const int maxToPlace = TriadGameSimulationState.boardSizeSq;
        for (var numToPlace = 1; numToPlace <= maxToPlace; numToPlace++)
        {
            var numPlaced = maxToPlace - numToPlace;

            numStatesForced *= numToPlace;
            if (numStatesForced <= MaxStatesToExplore)
            {
                minPlacedToExploreWithForced = numPlaced;
            }

            numStates *= numToPlace * ((numToPlace + 2) / 2) * ((numToPlace + 1) / 2);
            if (numStates <= MaxStatesToExplore)
            {
                minPlacedToExplore = numPlaced;
            }
        }
    }

    protected override bool CanRunRandomExploration(TriadGameSolver solver, TriadGameSimulationState gameState, int searchLevel)
    {
        var numPlacedThr = (gameState.forcedCardIdx < 0) ? minPlacedToExplore : minPlacedToExploreWithForced;

        return (searchLevel > 0) && (gameState.numCardsPlaced < numPlacedThr);
    }
}

/// <summary>
/// Aguments random search phase with score of game state to increase diffs between probability of initial steps
/// </summary>
public class TriadGameAgentCarloScored : TriadGameAgentCarloTheExplorer
{
    public const float StateWeight = 0.75f;
    public const float StateWeightDecay = 0.25f;

    public const float PriorityDefense = 1.0f;
    public const float PriorityDeck = 2.0f;
    public const float PriorityCapture = 3.5f;

    public override void Initialize(TriadGameSolver solver, int sessionSeed)
    {
        base.Initialize(solver, sessionSeed);
        agentName = "CarloScored";
    }

    protected override SolverResult FindWinningProbability(TriadGameSolver solver, TriadGameSimulationState gameState)
    {
        var result = base.FindWinningProbability(solver, gameState);
        var stateScore = CalculateStateScore(solver, gameState);
        var useWeight = Math.Max(0.0f, StateWeight - ((gameState.deckBlue.numPlaced - 1) * StateWeightDecay));

        var numWinsModified = ((result.numWins / result.numGames) * (1.0f - useWeight)) + (stateScore * useWeight);
        return new SolverResult(Math.Min(1.0f, numWinsModified), result.numDraws / result.numGames, 1);
    }

    public float CalculateStateScore(TriadGameSolver solver, TriadGameSimulationState gameState)
    {
        var (blueDefenseScore, blueCaptureScore) = CalculateBoardScore(solver, gameState);
        var deckScore = CalculateBlueDeckScore(solver, gameState);

        return ((blueDefenseScore * PriorityDefense) + (blueCaptureScore * PriorityCapture) + (deckScore * PriorityDeck)) / (PriorityDefense + PriorityDeck + PriorityCapture);
    }

    private (float, float) CalculateBoardScore(TriadGameSolver solver, TriadGameSimulationState gameState)
    {
        // for each blue card:
        //   for each side:
        //     find all numbers that can capture it
        //   normalize count of capturing numbers
        // normalize card capturing value
        // inverse => blue cards defensive value
        //
        // pct of blue in all cards => capture score

        var capturingSum = 0.0f;
        var numBlueCards = 0;

        for (var idx = 0; idx < gameState.board.Length; idx++)
        {
            var cardInst = gameState.board[idx];
            if (cardInst == null)
            {
                continue;
            }

            if (cardInst.owner == ETriadCardOwner.Blue)
            {
                var neis = TriadGameSimulation.cachedNeis[idx];

                var numCapturingValues = 0;
                var numValidSides = 0;
                for (var side = 0; side < 4; side++)
                {
                    if ((neis[side] >= 0) && (gameState.board[neis[side]] == null))
                    {
                        var cardNumber = cardInst.GetNumber((ETriadGameSide)side);
                        var numCaptures = 0;
                        for (var testValue = 1; testValue <= 10; testValue++)
                        {
                            var canCapture = CanBeCapturedWith(solver.simulation, cardNumber, testValue);
                            numCaptures += canCapture ? 1 : 0;
                        }

                        //Logger.WriteLine($"[{idx}].side:{side} card:{cardNumber} <- captures:{numCaptures}");
                        numValidSides++;
                        numCapturingValues += numCaptures;
                    }
                }

                capturingSum += (numValidSides > 0) ? (numCapturingValues / (numValidSides * 10.0f)) : 0.0f;
                numBlueCards++;
            }
        }

        var defenseScore = (numBlueCards > 0) ? (1.0f - (capturingSum / numBlueCards)) : 0.0f;
        var captureScore = Math.Min(1.0f, numBlueCards / 5.0f);

        return (defenseScore, captureScore);
    }

    private float CalculateBlueDeckScore(TriadGameSolver solver, TriadGameSimulationState gameState)
    {
        var blueCardScore = 0.0f;
        var numScoredBlueCards = 0;

        for (var idx = 0; idx < TriadDeckInstance.maxAvailableCards; idx++)
        {
            if ((gameState.deckBlue.availableCardMask & (1 << idx)) != 0)
            {
                var testCard = gameState.deckBlue.GetCard(idx);
                var cardScore = testCard.OptimizerScore;

                foreach (var mod in solver.simulation.modifiers)
                {
                    mod.OnScoreCard(testCard, ref cardScore);
                }

                blueCardScore += cardScore;
                numScoredBlueCards++;
            }
        }

        return (numScoredBlueCards > 0) ? (blueCardScore / numScoredBlueCards) : 0.0f;
    }

    private bool CanBeCapturedWith(TriadGameSimulation simulation, int defendingNum, int capturingNum)
    {
        if ((simulation.modFeatures & TriadGameModifier.EFeature.CaptureWeights) != 0)
        {
            var isReverseActive = (simulation.modFeatures & TriadGameModifier.EFeature.CaptureMath) != 0;

            foreach (var mod in simulation.modifiers)
            {
                mod.OnCheckCaptureCardWeights(null, -1, -1, isReverseActive, ref capturingNum, ref defendingNum);
            }
        }

        var isCaptured = (capturingNum > defendingNum);

        if ((simulation.modFeatures & TriadGameModifier.EFeature.CaptureMath) != 0)
        {
            foreach (var mod in simulation.modifiers)
            {
                mod.OnCheckCaptureCardMath(null, -1, -1, capturingNum, defendingNum, ref isCaptured);
            }
        }

        return isCaptured;
    }
}
