namespace HackF5.UnitySpy.Detail
{
    // mono source code: MonoTypeKind
    public enum MonoClassKind
    {
        // non-generic type
        Def = 1,
        // generic type definition
        GTg = 2,
        // generic instantiation
        GInst = 3,
        // generic parameter
        GParam = 4,
        // vector or array, bounded or not
        Array = 5,
        // pointer of function pointer
        Pointer = 6,
    }
}
