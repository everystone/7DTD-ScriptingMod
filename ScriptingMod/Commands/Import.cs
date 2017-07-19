﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JetBrains.Annotations;
using ScriptingMod.Exceptions;
using ScriptingMod.Extensions;
using ScriptingMod.Managers;
using Enumerable = UniLinq.Enumerable;

namespace ScriptingMod.Commands
{
    /*
     * TODO [P3]: Save prefabs in a subdirectory, but somehow allow also to load standard prefabs
     * TODO [P3]: Allow omitting the x y z to import prefab at previous position.Previous position must be saved for this first!
     * TODO [P2]: Tests outstanding:
     * [ ] Check that overwritten land claim blocks disappear and the protection with them correctly
     * [ ] Check that copied new land claim blocks found the claimed areas correctly
     * [ ] Check if overwritten beds are unsetting player's home point correctly
     * [ ] Check if imported beds are stored as new home point
     * [x] Test importing using the current players location
     */

    public class Import : ConsoleCmdAbstract
    {
        private static string _prefabsFolder = Path.GetFullPath(Utils.GetGameDir(global::Constants.cDirPrefabs));

        public override string[] GetCommands()
        {
            return new[] {"dj-import"};
        }

        public override string GetDescription()
        {
            return "Imports a prefab, optionally including all container content, sign texts, ownership, etc.";
        }

        public override string GetHelp()
        {
            // ----------------------------------(max length: 100 char)--------------------------------------------|
            return @"
                Imports a prefab from the folder /Data/Prefabs into the world. With the optional parameter ""/all""
                additional block metadata like container content, sign texts, ownership, etc. is also restored. For
                this to work the prefab must be exported with dj-export and include a ""tile entity"" file (.te).
                The prefab is placed facing north/east/up from the given position.
                Rotation can be 0 = unmodified, 1 = 90° right, 2 = 180°, 3 = 270° right.
                Usage:
                    1. dj-import [/all] <name>
                    2. dj-import [/all] <name> <rotation>
                    3. dj-import [/all] <name> <x> <y> <z>
                    4. dj-import [/all] <name> <x> <y> <z> <rotation>
                1. Imports the prefab at the current position.
                2. Imports the prefab at the current position with given rotation.
                3. Imports the prefab at the given position.
                4. Imports the prefab at the given position with the given rotation.
                ".Unindent();
        }

        public override void Execute(List<string> paramz, CommandSenderInfo senderInfo)
        {
            try
            {
                (string fileName, Vector3i pos1, int rotate, bool all) = ParseParams(paramz, senderInfo);

                // TODO [P2]: Either switch the following (TEST!) or pre-check if chunks are loaded
                // Note: Cannot switch, because prefab.load creates tile entities for the containers

                LoadPrefab(fileName, pos1, rotate, out Vector3i pos2);
                HashSet<Chunk> affectedChunks = GetAffectedChunks(pos1, pos2);

                // Even if loading tile entities fails, we still need to reload the already imported
                // prefab and print a success message about that (without metadata).
                try
                {
                    if (all)
                    {
                        LoadTileEntities(fileName, pos1, pos2, rotate);
                    }
                    SdtdConsole.Instance.Output($"Prefab {fileName} placed{(all ? " with block metdata" : "")} at {pos1} with rotation {rotate}.");
                }
                catch (Exception)
                {
                    SdtdConsole.Instance.Output($"Prefab {fileName} placed at {pos1} with rotation {rotate}, but importing block metadata failed.");
                    throw;
                }
                finally
                {
                    ScriptingMod.Managers.ChunkManager.ResetStability(affectedChunks);
                    ScriptingMod.Managers.ChunkManager.ReloadForClients(affectedChunks);
                }

                // TODO [P2]: Make a good alternative output when LoadTileEntities fails.
            }
            catch (FriendlyMessageException ex)
            {
                SdtdConsole.Instance.Output(ex.Message);
                Log.Out(ex.Message);
            }
            catch (Exception ex)
            {
                SdtdConsole.Instance.Output("Error occured during command execution: " + ex.Message + " [details in server log]");
                Log.Exception(ex);
            }
        }

        private static (string fileName, Vector3i pos1, int rotate, bool all)
            ParseParams(List<string> paramz, CommandSenderInfo senderInfo)
        {
            var all = false;

            if (paramz.Contains("/all"))
            {
                // Make copy and remove from list to make subsequent parsing easier
                paramz = new List<string>(paramz);
                paramz.Remove("/all");
                all = true;
            }

            var fileName = paramz[0];
            var tts = global::Constants.cExtPrefabs; // Cannot interpolate in string: https://youtrack.jetbrains.com/issue/RSRP-465524
            if (!File.Exists(Path.Combine(_prefabsFolder, fileName + ".xml")) || 
                !File.Exists(Path.Combine(_prefabsFolder, fileName + tts)))
                throw new FriendlyMessageException($"Could not find {fileName}.xml/{tts} in {_prefabsFolder}.");

            if (all && !File.Exists(Path.Combine(_prefabsFolder, fileName + ".te")))
                throw new FriendlyMessageException($"Could not find {fileName}.te in prefabs folder. This prefab does not have block metadata available, so you cannot use the /all option.");

            Vector3i pos1;
            if (paramz.Count == 1 || paramz.Count == 2)
            {
                pos1 = PlayerManager.GetPosition(senderInfo);
            }
            else
            {
                try
                {
                    pos1 = new Vector3i(int.Parse(paramz[1]), int.Parse(paramz[2]), int.Parse(paramz[3]));
                }
                catch (Exception)
                {
                    throw new FriendlyMessageException("At least one of the given coordinates is not a valid integer.");
                }
            }

            var rotate   = 0;
            if (paramz.Count == 2 || paramz.Count == 5)
            {
                rotate = paramz[paramz.Count - 1].ToInt()
                    ?? throw new FriendlyMessageException("Rotation value is not valid. Allowed values: 0, 1, 2, or 3");
            }

            return (fileName, pos1, rotate, all);
        }

        [NotNull]
        private static HashSet<Chunk> GetAffectedChunks(Vector3i pos1, Vector3i pos2)
        {
            var world = GameManager.Instance.World;
            var affectedChunks = new HashSet<Chunk>();
            for (int x = pos1.x; x <= pos2.x; x++)
            {
                for (int z = pos1.z; z <= pos2.z; z++)
                {
                    var chunk = world.GetChunkFromWorldPos(x, 0, z) as Chunk;
                    if (chunk == null)
                        throw new FriendlyMessageException("Area to export is too far away. Chunk not loaded on that area.");

                    affectedChunks.Add(chunk);
                }
            }
            return affectedChunks;
        }

        private static void LoadPrefab(string fileName, Vector3i pos1, int rotate, out Vector3i pos2)
        {
            Prefab prefab = new Prefab();
            if (!prefab.Load(fileName))
                throw new FriendlyMessageException($"Prefab {fileName} could not be loaded.");

            prefab.bCopyAirBlocks = true;
            prefab.bCopyAirBlocks = true;
            // DO NOT USE! Saving with this crashes the server.
            //prefab.bSleeperVolumes = true;

            for (int i = 0; i < rotate; i++)
                prefab.RotateY(false);

            prefab.CopyIntoLocal(GameManager.Instance.World.ChunkCache, new Vector3i(pos1.x, pos1.y, pos1.z), true, true);

            // Return for later
            pos2.x = pos1.x + prefab.size.x - 1;
            pos2.y = pos1.y + prefab.size.y - 1;
            pos2.z = pos1.z + prefab.size.z - 1;

            Log.Out($"Imported prefab {fileName} into area {pos1} to {pos2}.");
        }

        ///// <summary>
        ///// Removes all tile entities from blocks in the given area defined by the two points.
        ///// </summary>
        ///// <param name="pos1">Point South/West/Down in the area, i.e. smallest numbers</param>
        ///// <param name="pos2">Point North/East/Up in the area, i.e. highest numbers</param>
        //private static void RemoveTileEntities(Vector3i pos1, Vector3i pos2)
        //{
        //    var world = GameManager.Instance.World;
        //    var countRemoved = 0;

        //    for (int x = pos1.x; x <= pos2.x; x++)
        //    {
        //        for (int z = pos1.z; z <= pos2.z; z++)
        //        {
        //            var chunk = world.GetChunkFromWorldPos(x, 0, z) as Chunk;
        //            if (chunk == null)
        //            {
        //                Log.Warning($"Could not remove tile entities from blocks {x}, {pos1.y} to {pos2.y}, {z} because the chunk is not loaded.");
        //                continue;
        //            }

        //            for (int y = pos1.y; y <= pos2.y; y++)
        //            {
        //                var posInChunk = World.toBlock(x, y, z);
        //                if (chunk.GetTileEntity(posInChunk) == null)
        //                    continue;

        //                countRemoved++;
        //                chunk.RemoveTileEntityAt<TileEntity>(world, posInChunk);
        //            }
        //        }
        //    }
        //    Log.Out($"Removed {countRemoved} tile entities from area {pos1} to {pos2}.");
        //}

        private static void LoadTileEntities(string fileName, Vector3i pos1, Vector3i pos2, int rotate)
        {
            var filePath     = Path.Combine(_prefabsFolder, fileName + ".te");
            var world        = GameManager.Instance.World;
            var tileEntities = new Dictionary<Vector3i, TileEntity>(); // posInWorld => TileEntity
            int tileEntitiyCount;

            if (!File.Exists(filePath))
            {
                throw new FriendlyMessageException(
                    $"Block metadata could not be imported because file {Path.GetFileName(filePath)} does not exist in Prefabs folder. " +
                    "You can only import prefabs with the /all command if you exported them with the dj-export command before. See help for details.");
            }

            // Read all tile entities from file into dictionary
            using (var reader = new BinaryReader(new FileStream(filePath, FileMode.Open)))
            {
                // See Assembly-CSharp::Chunk.read() -> search "tileentity.read"
                tileEntitiyCount = reader.ReadInt32();                                      // [Int32]   number of tile entities
                for (int i = 0; i < tileEntitiyCount; i++)
                {
                    var posInPrefab = NetworkUtils.ReadVector3i(reader);                    // [3xInt32] position relative to prefab
                    posInPrefab     = RotatePosition(posInPrefab, pos2 - pos1, rotate);

                    var posInWorld  = new Vector3i(pos1.x + posInPrefab.x, pos1.y + posInPrefab.y, pos1.z + posInPrefab.z);
                    var posInChunk  = World.toBlock(posInWorld);

                    var chunk = world.GetChunkFromWorldPos(posInWorld) as Chunk;

                    var tileEntityType = (TileEntityType)reader.ReadInt32();                // [Int32]   TileEntityType enum
                    TileEntity tileEntity = TileEntity.Instantiate(tileEntityType, chunk ?? new Chunk()); // read(..) fails if chunk is null
                    tileEntity.read(reader, TileEntity.StreamModeRead.Persistency);         // [dynamic] tile entity data depending on type
                    tileEntity.localChunkPos = posInChunk; // adjust to new location

                    // Check cannot be done earlier, because we MUST do the file reads regardless in order to continue reading
                    if (chunk == null)
                    {
                        Log.Warning($"Could not import tile entity for block {posInWorld} because the chunk is not loaded.");
                        continue;
                    }

                    tileEntities.Add(posInWorld, tileEntity);
                }
            }

            // Go through every block and replace tile entities
            for (int x = pos1.x; x <= pos2.x; x++)
            {
                for (int z = pos1.z; z <= pos2.z; z++)
                {
                    var chunk = world.GetChunkFromWorldPos(x, 0, z) as Chunk;
                    if (chunk == null)
                    {
                        Log.Warning($"Could not remove tile entities from blocks {x}, {pos1.y} to {pos2.y}, {z} because the chunk is not loaded.");
                        continue;
                    }

                    for (int y = pos1.y; y <= pos2.y; y++)
                    {
                        // Remove default empty tile entity from prefab
                        chunk.RemoveTileEntityAt<TileEntity>(world, World.toBlock(x, y, z));

                        // Add previously loaded tile entity if we have one
                        var tileEntity = tileEntities.GetValue(new Vector3i(x, y, z));
                        if (tileEntity != null)
                            chunk.AddTileEntity(tileEntity);
                    }
                }
            }

            Log.Out($"Imported {tileEntitiyCount} tile entities for prefab {fileName} into area {pos1} to {pos2}.");
        }

        /// <summary>
        /// Returns rotated coordinates of the given position within an area of 0,0,0 to pos2.
        /// Only x and z are rotated, y stays the same.
        /// </summary>
        /// <param name="pos">The point in the area to rotate</param>
        /// <param name="pos2">Most North/East point of the area, e.g. the higher integers</param>
        /// <param name="rotate">0 = unmodified, 1 = 90° right, 2 = 180°, 3 = 270° right</param>
        /// <returns></returns>
        private static Vector3i RotatePosition(Vector3i pos, Vector3i pos2, int rotate)
        {
            switch (rotate)
            {
                case 0: // 0°
                    return pos;
                case 1: // 90° right
                    return new Vector3i(pos.z, pos.y, pos2.z - pos.x);
                case 2: // 180°
                    return new Vector3i(pos2.x - pos.x, pos.y, pos2.z - pos.z);
                case 3: // 270°
                    return new Vector3i(pos2.x - pos.z, pos.y, pos.x);
                default:
                    throw new ArgumentException("Rotation must be either 0, 1, 2, or 3", nameof(rotate));
            }
        }

    }
}