using MemolineDemo.Editing;

namespace MemolineDemo;

internal static class UndoCapacityChecks
{
    internal static int Run()
    {
        int checks=0;
        void Check(bool condition,string message)
        { if(!condition)throw new InvalidOperationException(message);checks++; }
        Check(UndoCapacityPolicy.Required(1)==6,"Even one stroke needs five spare undo entries.");
        Check(UndoCapacityPolicy.Required(53)==58,"Capacity must include the complete overlap closure.");
        Check(!UndoCapacityPolicy.Check(1,null,false).Allowed,"Unknown capacity must require confirmation for fewer than fifty strokes.");
        Check(UndoCapacityPolicy.Check(1,null,true).Allowed,"An explicit capacity confirmation permits unknown capacity.");
        Check(!UndoCapacityPolicy.Check(53,57,true).Allowed,"A known insufficient capacity cannot be overridden by a checkbox.");
        Check(UndoCapacityPolicy.Check(53,58,false).Allowed,"A verified sufficient capacity needs no unknown-capacity confirmation.");
        Check(!UndoCapacityPolicy.Check(0,null,true).Allowed,"An empty selection cannot be installed as precise undo.");
        return checks;
    }
}
