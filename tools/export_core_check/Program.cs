// Copies the CURRENT src/PakuPort.Core into each directory given, with the same copier `export`
// and `remake capture` use (ExportCore.Copiar). Run from the repo root; check_fleet.py drives it.
if (args.Length == 0)
{
    Console.Error.WriteLine("uso: export_core_check <dir-de-export> [...]   (desde la raiz del repo)");
    return 1;
}
foreach (var path in args) ExportCore.Copiar(path);
return 0;
