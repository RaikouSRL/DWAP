using Archipelago.Core.AvaloniaGUI.ViewModels;
using DWAP.Models;
using ReactiveUI;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DWAP.ViewModels
{
    // Backs the "Recruitment Tracker" custom control window (opened from the
    // main client's "Open Custom Controls" button). Two grids, styled after
    // the user's reference tracker site: Recruitments (every recruitable
    // Digimon, tile colored gray/yellow/green for none/Soul-only/recruited)
    // and Wild Encounters (every Wild Digimon location, tile green once
    // beaten). Recruited and Soul are tracked independently because in the
    // randomised game a Digimon can show as recruited in memory for a
    // moment without its Soul owned yet - see EnsureSouls(), which reverts
    // that - so the tracker mirrors that same distinction rather than
    // collapsing it into one status.
    public class RecruitmentTrackerViewModel : ViewModelBase
    {
        public ObservableCollection<RecruitmentStatus> Recruitments { get; }
        public ObservableCollection<WildEncounterStatus> WildEncounters { get; }

        private string _summary = "Recruited: 0/0    Souls: 0/0";
        public string Summary
        {
            get => _summary;
            set => this.RaiseAndSetIfChanged(ref _summary, value);
        }

        private string _wildSummary = "Beaten: 0/0";
        public string WildSummary
        {
            get => _wildSummary;
            set => this.RaiseAndSetIfChanged(ref _wildSummary, value);
        }

        public RecruitmentTrackerViewModel()
        {
            Recruitments = new ObservableCollection<RecruitmentStatus>();
            WildEncounters = new ObservableCollection<WildEncounterStatus>();
        }

        public RecruitmentTrackerViewModel(
            IEnumerable<string> digimonNames,
            IEnumerable<(string Name, string SpriteKey)> wildEncounterNames) : this()
        {
            foreach (var name in digimonNames.OrderBy(x => x))
            {
                Recruitments.Add(new RecruitmentStatus(name));
            }
            foreach (var (name, spriteKey) in wildEncounterNames.OrderBy(x => x.Name))
            {
                WildEncounters.Add(new WildEncounterStatus(name, spriteKey));
            }
            RefreshSummary();
        }

        public void RefreshSummary()
        {
            var recruitedCount = Recruitments.Count(x => x.IsRecruited);
            var soulCount = Recruitments.Count(x => x.HasSoul);
            Summary = $"Recruited: {recruitedCount}/{Recruitments.Count}    Souls: {soulCount}/{Recruitments.Count}";

            var defeatedCount = WildEncounters.Count(x => x.IsDefeated);
            WildSummary = $"Beaten: {defeatedCount}/{WildEncounters.Count}";
        }
    }
}
