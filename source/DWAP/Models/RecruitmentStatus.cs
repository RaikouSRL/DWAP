using Avalonia.Media;
using ReactiveUI;

namespace DWAP.Models
{
    // Backs one row of the Recruitment Tracker custom control - one entry
    // per recruitable Digimon, tracking whether it has been recruited
    // in-game and whether its Soul item has been received from Archipelago.
    // ReactiveObject so the tracker window updates live as EnsureRecruitmentTracker()
    // refreshes these every tick, same as the rest of the client's polling.
    public class RecruitmentStatus : ReactiveObject
    {
        public string Name { get; }
        public IImage Sprite { get; }

        // Gray (nothing yet) -> yellow (Soul received, not recruited yet) ->
        // green (Soul received and recruited), matching the tile colors the
        // user's reference tracker site uses.
        private static readonly IBrush NoneBrush = new SolidColorBrush(Color.Parse("#868686"));
        private static readonly IBrush SoulOnlyBrush = new SolidColorBrush(Color.Parse("#C9A227"));
        private static readonly IBrush RecruitedBrush = new SolidColorBrush(Color.Parse("#008641"));

        private bool _isRecruited;
        public bool IsRecruited
        {
            get => _isRecruited;
            set
            {
                this.RaiseAndSetIfChanged(ref _isRecruited, value);
                this.RaisePropertyChanged(nameof(StatusColor));
            }
        }

        private bool _hasSoul;
        public bool HasSoul
        {
            get => _hasSoul;
            set
            {
                this.RaiseAndSetIfChanged(ref _hasSoul, value);
                this.RaisePropertyChanged(nameof(StatusColor));
            }
        }

        public IBrush StatusColor => IsRecruited && HasSoul ? RecruitedBrush : HasSoul ? SoulOnlyBrush : NoneBrush;

        public RecruitmentStatus(string name)
        {
            Name = name;
            Sprite = Helpers.LoadDigimonSprite(name);
        }
    }
}
