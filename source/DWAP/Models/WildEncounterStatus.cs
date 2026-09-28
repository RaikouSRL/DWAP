using Avalonia.Media;
using ReactiveUI;

namespace DWAP.Models
{
    // Backs one row of the Recruitment Tracker's "Wild Encounters" grid -
    // one entry per Wild Digimon location, tracking whether it's been beaten.
    public class WildEncounterStatus : ReactiveObject
    {
        public string Name { get; }
        public IImage Sprite { get; }

        private static readonly IBrush NoneBrush = new SolidColorBrush(Color.Parse("#868686"));
        private static readonly IBrush DefeatedBrush = new SolidColorBrush(Color.Parse("#008641"));

        private bool _isDefeated;
        public bool IsDefeated
        {
            get => _isDefeated;
            set
            {
                this.RaiseAndSetIfChanged(ref _isDefeated, value);
                this.RaisePropertyChanged(nameof(StatusColor));
            }
        }

        public IBrush StatusColor => IsDefeated ? DefeatedBrush : NoneBrush;

        public WildEncounterStatus(string name, string spriteKey)
        {
            Name = name;
            Sprite = Helpers.LoadDigimonSprite(spriteKey);
        }
    }
}
