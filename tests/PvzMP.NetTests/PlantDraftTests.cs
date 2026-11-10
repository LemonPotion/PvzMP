using System.Collections.Generic;
using PvzMP.Core.Session;
using Xunit;

namespace PvzMP.NetTests
{
    /// <summary>
    /// The seed chooser's draft: one plant type belongs to one seat until that seat puts it back, and a
    /// player that leaves frees everything it held. The host's UI is driven from this map, so the rules that
    /// keep two players from choosing the same plant live here instead of in a screenshot.
    /// </summary>
    public class PlantDraftTests
    {
        [Fact]
        public void TheFirstSeatTakesAPlantAndEveryoneElseIsRefused()
        {
            PlantDraft draft = new PlantDraft();

            Assert.True(draft.TryTake(7, 1));
            Assert.Equal(1, draft.OwnerOf(7));

            Assert.False(draft.TryTake(7, 2));
            Assert.Equal(1, draft.OwnerOf(7));
        }

        [Fact]
        public void ASeatThatAlreadyHoldsAPlantMayTakeItAgain()
        {
            PlantDraft draft = new PlantDraft();

            Assert.True(draft.TryTake(7, 1));
            Assert.True(draft.TryTake(7, 1));
        }

        [Fact]
        public void OnlyTheOwnerCanPutAPlantBack()
        {
            PlantDraft draft = new PlantDraft();
            draft.TryTake(7, 1);

            Assert.False(draft.Release(7, 2));
            Assert.Equal(1, draft.OwnerOf(7));

            Assert.True(draft.Release(7, 1));
            Assert.Equal(-1, draft.OwnerOf(7));
            Assert.True(draft.TryTake(7, 2));
        }

        [Fact]
        public void AFreePlantCanBeTakenByAnybody()
        {
            PlantDraft draft = new PlantDraft();

            Assert.Equal(-1, draft.OwnerOf(42));
            Assert.True(draft.TryTake(42, 3));
        }

        [Fact]
        public void APlayerThatLeavesFreesEverythingItHeld()
        {
            PlantDraft draft = new PlantDraft();
            draft.TryTake(1, 2);
            draft.TryTake(2, 2);
            draft.TryTake(3, 3);

            List<int> freed = draft.ReleasePeer(2);

            Assert.Equal(2, freed.Count);
            Assert.Contains(1, freed);
            Assert.Contains(2, freed);
            Assert.Equal(-1, draft.OwnerOf(1));
            Assert.Equal(-1, draft.OwnerOf(2));
            Assert.Equal(3, draft.OwnerOf(3));
        }

        [Fact]
        public void ASnapshotCanBeReadWhileTheMapChanges()
        {
            PlantDraft draft = new PlantDraft();
            draft.TryTake(1, 2);
            draft.TryTake(2, 3);

            KeyValuePair<int, int>[] pairs = draft.Snapshot();
            Assert.Equal(2, pairs.Length);

            draft.Clear();
            Assert.Equal(-1, draft.OwnerOf(1));
            Assert.Equal(2, pairs.Length);
        }
    }
}
