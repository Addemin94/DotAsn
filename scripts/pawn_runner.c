/*
 * A minimal host runner for Pawn 4 programs with 32-bit cells, the cell size of small devices.
 * It provides the console and core natives, runs main() and returns its exit code.
 * Built by scripts/pawn_vectors.py from a Pawn 4 source tree.
 */
#include <stdio.h>

#include "amx.h"
#include "amxaux.h"

int AMXEXPORT AMXAPI amx_ConsoleInit(AMX* amx);
int AMXEXPORT AMXAPI amx_CoreInit(AMX* amx);

int main(int argc, char* argv[]) {
    if (argc < 2) {
        fprintf(stderr, "usage: %s program.amx\n", argv[0]);
        return 2;
    }

    AMX amx;
    int error = aux_LoadProgram(&amx, argv[1], NULL);
    if (error != AMX_ERR_NONE) {
        fprintf(stderr, "load: %s\n", aux_StrError(error));
        return 2;
    }

    amx_ConsoleInit(&amx);
    error = amx_CoreInit(&amx);
    if (error != AMX_ERR_NONE) {
        fprintf(stderr, "natives: %s\n", aux_StrError(error));
        return 2;
    }

    cell result = 0;
    error = amx_Exec(&amx, &result, AMX_EXEC_MAIN);
    if (error != AMX_ERR_NONE) {
        fprintf(stderr, "run: %s\n", aux_StrError(error));
        return 2;
    }

    aux_FreeProgram(&amx);
    return (int)result;
}
