using RecompOne.Runtime.Memory;
using Recompiled;

// Entry point for the King's Field II (SLPS-00069) port.
//
// RecompOne generates its own Program.cs into generated/ if one is missing; this
// file takes that role instead so startup stays hand-editable. Custom init and
// patching hooks go here, before Entry.Run.

var memory = new PSMemory();
Entry.Run(memory, args.Length > 0 ? args[0] : null);
return 0;
