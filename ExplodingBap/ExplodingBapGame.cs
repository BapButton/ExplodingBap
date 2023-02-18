using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using BAP.Helpers;
using BAP.Types;
using MessagePipe;
using Microsoft.Extensions.Logging;

namespace ExplodingBap
{

    internal class ExplodingBapRow
    {
        internal bool ReversedDirection { get; set; }
        internal List<ExplodingBapImageInfo> OrderedImages { get; set; } = default!;
        internal List<string> NodeIdsOrderedLeftToRight { get; set; } = new();
    }

    internal class ExplodingBapImageInfo
    {
        public int ImageId { get; set; }
        public int ImageTrackingId { get; set; }
        public bool IsHidden { get; set; }
        public List<string> CurrentNodes { get; set; } = new();
    }

    internal class ActiveButtonPress
    {
        public string NodeId { get; set; } = "";
        public int PressedAtFrame { get; set; }
        public List<(int imageId, int imageTrackingId)> ImageIds { get; set; } = new();
    }

    internal class ColumnInfo
    {
        public int ColumnId { get; set; }
        public List<string> NodeIds { get; set; } = new();
    }

    internal class ExplosionTracker
    {
        internal List<string> InitialNodeIds { get; set; } = new();
        internal List<string> ActiveNodeIds { get; set; } = new();
        internal int FrameIdOfCurrentExplosion { get; set; }
    }


    public class ExplodingBapGame : IBapGame
    {

        public bool IsGameRunning { get; set; }
        ILogger<ExplodingBapGame> Logger { get; set; }
        IBapMessageSender MsgSender { get; set; } = default!;
        ConcurrentQueue<string> NodeIdsPressed { get; set; } = new();
        List<ulong[,]> PossibleImages { get; set; } = new();
        ILayoutProvider LayoutProvider { get; set; } = default!;
        bool ExplosionInProcess;
        ExplosionTracker? currentExplosionTracker = null;
        CancellationTokenSource timerTokenSource = new();
        IDisposable subscriptions = default!;
        List<ActiveButtonPress> ActiveButtonPresses { get; set; } = new();
        List<ExplodingBapRow> Rows { get; set; } = new();
        List<ColumnInfo> Columns { get; set; } = new();
        List<int> firstAndLastColumnIds = new();
        int FrameTrackingCount = 0;
        ISubscriber<ButtonPressedMessage> ButtonPressedPipe { get; set; } = default!;
        public int SpeedMultiplier = 8;
        public int ImageTrackingId = 0;
        public int offset = 0;
        public int CorrectPresses = 0;
        PeriodicTimer? timer = null;
        public ExplodingBapGame(ILogger<ExplodingBapGame> logger, IBapMessageSender messageSender, ILayoutProvider layoutProvider, ISubscriber<ButtonPressedMessage> buttonPressedPipe)
        {
            Logger = logger;
            MsgSender = messageSender;
            LayoutProvider = layoutProvider;
            ButtonPressedPipe = buttonPressedPipe;
            var bag = DisposableBag.CreateBuilder();
            ButtonPressedPipe.Subscribe((x) => ButtonPressed(x)).AddTo(bag);
            subscriptions = bag.Build();
        }

        private void ClearOutTheStuffForANewRound()
        {
            ExplosionInProcess = false;
            CorrectPresses = 0;
            offset = 8;
            Columns = new();
            currentExplosionTracker = null;
        }


        public async Task<bool> Start()
        {
            ClearOutTheStuffForANewRound();
            IsGameRunning = true;

            Rows = new();

            if (PossibleImages.Count == 0)
            {
                string path = FilePathHelper.GetFullPath<ExplodingBapGame>("ExplodingBap.bmp");
                PossibleImages = new SpriteParser(path).GetCustomMatricesFromCustomSprite();
            }
            if (LayoutProvider == null || LayoutProvider?.CurrentButtonLayout == null)
            {
                MsgSender.SendUpdate("Exploding Bap requires a button Layout", fatalError: true);
                await ForceEndGame();
                return false;
            }

            foreach (var row in LayoutProvider.CurrentButtonLayout.ButtonPositions.GroupBy(t => t.RowId).OrderBy(t => t.Key))
            {
                int arrayWidth = row.Count() + 1;
                ExplodingBapRow newRow = new()
                {
                    ReversedDirection = row.Key % 2 == 0,
                    OrderedImages = new(arrayWidth),
                    NodeIdsOrderedLeftToRight = row.OrderBy(t => t.ColumnId).Select(t => t.ButtonId).ToList()
                };

                foreach (var node in row)
                {
                    ColumnInfo? ci = Columns.FirstOrDefault(t => t.ColumnId == node.ColumnId);
                    if (ci == null)
                    {
                        ci = new() { ColumnId = node.ColumnId };
                        Columns.Add(ci);
                    }
                    ci.NodeIds.Add(node.ButtonId);
                }
                for (int i = 0; i < arrayWidth; i++)
                {
                    newRow.OrderedImages.Add(new ExplodingBapImageInfo() { ImageId = 1, IsHidden = true, ImageTrackingId = ++ImageTrackingId });
                }
                newRow.OrderedImages[0].ImageId = BapBasicGameHelper.GetRandomInt(0, PossibleImages.Count);
                newRow.OrderedImages[0].IsHidden = false;
                Rows.Add(newRow); ;
            }
            firstAndLastColumnIds.Add(Columns.OrderBy(t => t.ColumnId).First().ColumnId);
            firstAndLastColumnIds.Add(Columns.OrderByDescending(t => t.ColumnId).First().ColumnId);
            MoveToNextFrame();
            //As we are not awaiting this task. It will just keep running until the cancellation token fires.
            Task TimerTask = StartGameFrameTicker();

            return true;
        }

        private async Task StartGameFrameTicker()
        {
            if (timerTokenSource.IsCancellationRequested)
            {
                timerTokenSource = new();

            }
            timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
            var timerToken = timerTokenSource.Token;

            while (await timer.WaitForNextTickAsync(timerToken))
            {
                try
                {
                    MoveToNextFrame();
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error in Move To Next Frame Method");
                }
            };
            Logger.LogError("The Timer Has Stopped");
        }

        private void ShiftEverythingAndShowIt()
        {
            List<(string nodeId, ButtonImage image)> images = new();
            if (offset == 0)
            {
                foreach (var row in Rows)
                {
                    //if an image has fallen off the map then add a new one.
                    row.OrderedImages.RemoveAt(row.OrderedImages.Count - 1);
                    row.OrderedImages.Insert(0, new ExplodingBapImageInfo() { ImageId = BapBasicGameHelper.GetRandomInt(0, PossibleImages.Count), ImageTrackingId = ++ImageTrackingId });

                }
                offset = 7;
            }
            else
            {
                offset--;
            }
            foreach (var row in Rows)
            {
                images.AddRange(GenerateImagesForRow(row));
            }


            foreach (var image in images)
            {
                MsgSender.SendImage(image.nodeId, image.image);
            }
        }

        private List<(string NodeId, ButtonImage buttonImage)> GenerateImagesForRow(ExplodingBapRow row)
        {
            int fullFrameLength = row.NodeIdsOrderedLeftToRight.Count * 8;
            List<(string nodeId, ButtonImage buttonImage)> images = new();
            ulong[,] bigMatrix = new ulong[8, fullFrameLength];
            for (int i = 0; i < row.OrderedImages.Count; i++)
            {
                var imageInfo = row.OrderedImages[i];
                if (imageInfo != null && !imageInfo.IsHidden)
                {
                    imageInfo.CurrentNodes = new List<string>();
                    ulong[,] image = PossibleImages[imageInfo.ImageId];
                    int startingPosition = 0;
                    //An offset of 7 means that we are displaying fully on the button and about to get a new image
                    if (row.ReversedDirection)
                    {
                        startingPosition = (fullFrameLength - 8 + offset) - (8 * i);
                        int mainLocation = (row.NodeIdsOrderedLeftToRight.Count - 1) - i;
                        if (mainLocation >= 0)
                        {
                            imageInfo.CurrentNodes.Add(row.NodeIdsOrderedLeftToRight[mainLocation]);
                        }
                        if (offset > 0 && mainLocation + 1 < row.NodeIdsOrderedLeftToRight.Count)
                        {
                            imageInfo.CurrentNodes.Add(row.NodeIdsOrderedLeftToRight[mainLocation + 1]);
                        }
                    }
                    else
                    {
                        startingPosition = 0 - offset + (8 * i);
                        if (i < row.NodeIdsOrderedLeftToRight.Count)
                        {
                            imageInfo.CurrentNodes.Add(row.NodeIdsOrderedLeftToRight[i]);
                        }
                        if (offset > 0 && i - 1 >= 0)
                        {
                            imageInfo.CurrentNodes.Add(row.NodeIdsOrderedLeftToRight[i - 1]);
                        }
                    }

                    AnimationHelper.MergeMatrices(bigMatrix, image, 0, startingPosition, false);
                }
            }
            //Now that the big matrix is made we can cut it up into frames;
            for (int i = 0; i < row.NodeIdsOrderedLeftToRight.Count; i++)
            {
                images.Add((row.NodeIdsOrderedLeftToRight[i], new ButtonImage(bigMatrix.ExtractMatrix(0, (i * 8)))));
            }
            return images;
        }

        private void MoveToNextFrame()
        {

            if (!ExplosionInProcess)
            {
                EvaluateButtonPresses();
                EvaluateIfTheGameHasEnded();
            }

            if (ExplosionInProcess)
            {

                if (FrameTrackingCount % 4 == 0)
                {
                    ShowTheExplosion();
                }

            }
            else
            {
                if (FrameTrackingCount % SpeedMultiplier == 0)
                {
                    ShiftEverythingAndShowIt();
                }

            }
            FrameTrackingCount++;

        }
        private void ButtonPressed(ButtonPressedMessage buttonPressedMessage)
        {
            NodeIdsPressed.Enqueue(buttonPressedMessage.NodeId);
        }

        private void ShowTheExplosion()
        {
            List<(string nodeId, ButtonImage buttonImage)> allCurrentImages = new();
            foreach (var row in Rows)
            {
                allCurrentImages.AddRange(GenerateImagesForRow(row));
            }
            if (currentExplosionTracker != null)
            {
                if (currentExplosionTracker.ActiveNodeIds.Count == 0)
                {
                    currentExplosionTracker.ActiveNodeIds = currentExplosionTracker.InitialNodeIds;
                }
                bool endTheGame = false;
                foreach (var nodeId in currentExplosionTracker.ActiveNodeIds)
                {
                    var currentMessage = allCurrentImages.FirstOrDefault(t => t.nodeId == nodeId);
                    if (currentMessage != default)
                    {
                        ulong[,] explosionOverLay = new ulong[8, 8];
                        int overLayRow = 0;
                        int overLayColumn = 0;

                        ulong white = new BapColor(255, 255, 255).LongColor;
                        if (currentExplosionTracker.FrameIdOfCurrentExplosion >= 0)
                        {
                            ulong[,] tempOverlay = new ulong[2, 2];
                            tempOverlay[0, 0] = white;
                            tempOverlay[0, 1] = white;
                            tempOverlay[1, 0] = white;
                            tempOverlay[1, 1] = white;
                            explosionOverLay.MergeMatrices(tempOverlay, 3, 3);

                        }
                        if (currentExplosionTracker.FrameIdOfCurrentExplosion >= 3)
                        {
                            ulong[,] tempOverlay = new ulong[4, 4];
                            tempOverlay[0, 0] = white;
                            tempOverlay[0, 3] = white;
                            tempOverlay[3, 0] = white;
                            tempOverlay[0, 3] = white;
                            explosionOverLay.MergeMatrices(tempOverlay, 2, 2);
                        }
                        if (currentExplosionTracker.FrameIdOfCurrentExplosion >= 2)
                        {
                            ulong[,] tempOverlay = new ulong[6, 6];
                            tempOverlay[0, 0] = white;
                            tempOverlay[0, 5] = white;
                            tempOverlay[1, 4] = white;
                            tempOverlay[3, 5] = white;
                            tempOverlay[5, 2] = white;
                            tempOverlay[5, 4] = white;
                            explosionOverLay.MergeMatrices(tempOverlay, 1, 1);
                        }
                        if (currentExplosionTracker.FrameIdOfCurrentExplosion >= 3)
                        {
                            ulong[,] tempOverlay = new ulong[8, 8];
                            for (int i = 0; i < (8 * currentExplosionTracker.FrameIdOfCurrentExplosion); i++)
                            {
                                tempOverlay[Random.Shared.Next(0, 8), Random.Shared.Next(0, 8)] = white;
                            }
                            explosionOverLay.MergeMatrices(tempOverlay, 0, 0);
                        }
                        if (currentExplosionTracker.FrameIdOfCurrentExplosion >= 10)
                        {
                            for (int i = 0; i < 8; i++)
                            {
                                for (int j = 0; j < 8; j++)
                                {
                                    explosionOverLay[i, j] = white;
                                }
                            }
                            endTheGame = true;
                        }
                        var currentMatrix = currentMessage.buttonImage.GetImageMatrix();
                        currentMatrix.MergeMatrices(explosionOverLay, overLayRow, overLayColumn);

                        MsgSender.SendImage(currentMessage.nodeId, new ButtonImage(currentMatrix));

                    }

                }
                if (endTheGame)
                {
                    ForceEndGame();
                }
                currentExplosionTracker.FrameIdOfCurrentExplosion++;
            }

        }


        private void EvaluateIfTheGameHasEnded()
        {
            if (offset == 0)
            {
                foreach (var column in Columns.Where(t => !firstAndLastColumnIds.Contains(t.ColumnId)))
                {
                    List<(int imageId, string nodeId)> foundImages = new();
                    foreach (var nodeId in column.NodeIds)
                    {
                        var showingImage = Rows.SelectMany(t => t.OrderedImages).Where(t => t.IsHidden == false && t.CurrentNodes.Contains(nodeId)).FirstOrDefault();
                        if (showingImage != null)
                        {
                            if (foundImages.Any(t => t.imageId == showingImage.ImageId))
                            {
                                ExplosionInProcess = true;
                                if (currentExplosionTracker == null)
                                {
                                    currentExplosionTracker = new ExplosionTracker();
                                }
                                currentExplosionTracker.InitialNodeIds.Add(nodeId);
                                currentExplosionTracker.InitialNodeIds.AddRange(foundImages.Where(t => t.imageId == showingImage.ImageId).Select(t => t.nodeId));
                                return;
                            }
                            foundImages.Add((showingImage.ImageId, nodeId));
                        }
                    }
                }
            }

        }

        private void EvaluateButtonPresses()
        {
            //nodeIds live for a while
            while (NodeIdsPressed.TryDequeue(out string? nodeId))
            {
                List<(int imageIds, int imageTrackerId)> imageIds = Rows.SelectMany(t => t.OrderedImages).Where(t => t.IsHidden == false && t.CurrentNodes.Contains(nodeId)).Select(t => (t.ImageId, t.ImageTrackingId)).ToList();
                ActiveButtonPresses.Add(new()
                {
                    NodeId = nodeId,
                    PressedAtFrame = FrameTrackingCount,
                    ImageIds = imageIds
                }); ;
            }
            foreach (var ci in Columns)
            {
                var pressedNodesInThisColumn = ActiveButtonPresses.Where(t => ci.NodeIds.Contains(t.NodeId)).ToList();
                if (pressedNodesInThisColumn.Count() > 1)
                {
                    foreach (var imageGroup in pressedNodesInThisColumn.SelectMany(t => t.ImageIds).GroupBy(t => t.imageId).Where(t => t.Count() > 1))
                    {
                        foreach (var imagesToRemove in imageGroup)
                        {
                            foreach (var imageToHide in Rows.SelectMany(t => t.OrderedImages).Where(t => t.ImageTrackingId == imagesToRemove.imageTrackingId))
                            {
                                imageToHide.IsHidden = true;
                            }
                        }
                    }
                }
            }
            //Clean up old NodeIds
            ActiveButtonPresses.RemoveAll(t => t.PressedAtFrame + (6 * SpeedMultiplier) < FrameTrackingCount);
        }





        public Task<bool> ForceEndGame()
        {
            timerTokenSource.Cancel();
            IsGameRunning = false;
            MsgSender.ClearButtons();
            MsgSender.SendUpdate("Game Force Ended", true);
            return Task.FromResult(true); ;
        }
        public void Dispose()
        {
            if (timerTokenSource != null)
            {
                timerTokenSource.Cancel();
                timerTokenSource.Dispose();
            }
            if (subscriptions != null)
            {
                subscriptions.Dispose();
            }
        }

    }
}
