using BAP.Types;
using MessagePipe;
using Microsoft.AspNetCore.Components;

namespace ExplodingBap.Components
{
    [GamePage("Exploding Bap", "Can you hit the matches before they explode", UniqueId = "c4843f82-00cc-47df-8766-ce34744ac981")]
    public partial class ExplodingBap : IGamePage, IDisposable
    {
        private string LastMessage = "";
        private bool showLogs { get; set; } = false;
        [Inject]
        IGameProvider GameHandler { get; set; } = default!;
        [Inject]
        ISubscriber<GameEventMessage> GameEventPipe { get; set; } = default!;
        IDisposable Subscriptions { get; set; } = default!;
        protected override void OnInitialized()
        {
            var bag = DisposableBag.CreateBuilder();
            GameEventPipe.Subscribe(async (x) => await GameUpdate(x)).AddTo(bag);
            Subscriptions = bag.Build();
            base.OnInitialized();
        }

        async Task GameUpdate(GameEventMessage e)
        {
            LastMessage = e.Message;
            await InvokeAsync(() =>
            {
                StateHasChanged();
            });
        }


        async Task<bool> ToggleLogs()
        {
            showLogs = !showLogs;
            await InvokeAsync(() =>
            {
                StateHasChanged();
            });
            return true;
        }

        async Task<bool> StartGame()
        {
            if (GameHandler.CurrentGame == null)
            {
                GameHandler.UpdateToNewGameType(typeof(ExplodingBapGame));
            }
            if (GameHandler.CurrentGame != null)
            {
                await GameHandler.CurrentGame.Start();
                await InvokeAsync(() =>
                {
                    StateHasChanged();
                });
                return true;
            }
            return false;
        }

        async Task<bool> EndGame()
        {
            if (GameHandler.CurrentGame != null)
            {
                await GameHandler.CurrentGame.ForceEndGame();
                await InvokeAsync(() =>
                {
                    StateHasChanged();
                });
                return true;
            }
            return false;
        }

        public void Dispose()
        {
            if (Subscriptions != null)
            {
                Subscriptions.Dispose();
            }
        }

        public Task<bool> NodesChangedAsync(NodeChangeMessage nodeChangeMessage)
        {
            return Task.FromResult(true);
        }

        public Task<bool> LayoutChangedAsync()
        {
            return Task.FromResult(true);
        }

        public Task<bool> GameUpdateAsync(GameEventMessage gameEventMessage)
        {
            return Task.FromResult(true);
        }
    }
}
