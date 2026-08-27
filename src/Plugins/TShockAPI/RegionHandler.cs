/*
TShock, a server mod for Terraria
Copyright (C) 2011-2019 Pryaxis & TShock Contributors
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.
You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using TrProtocol.NetPackets;
using TShockAPI.DB;
using TShockAPI.Extension;
using TShockAPI.Hooks;
using UnifierTSL.Events.Core;
using UnifierTSL.Events.Handlers;

namespace TShockAPI
{
	/// <summary>
	/// Represents TShock's Region subsystem. This subsystem is in charge of executing region related logic, such as
	/// setting temp points or invoking region events.
	/// </summary>
	internal sealed class RegionHandler : IDisposable
	{
		private readonly RegionManager _regionManager;

		/// <summary>
		/// Initializes a new instance of the <see cref="RegionHandler"/> class with the specified <see cref="RegionManager"/> instance.
		/// </summary>
		/// <param name="regionManager">The <see cref="RegionManager"/> instance.</param>
		public RegionHandler(RegionManager regionManager)
		{
			_regionManager = regionManager;

			NetPacketHandler.Register<PlayerControls>(OnPlayerUpdate, HandlerPriority.Normal);
			NetPacketHandler.Register<TileChange>(OnTileEdit, HandlerPriority.Normal);
            NetPacketHandler.Register<GemLockToggle>(OnGemLockToggle, HandlerPriority.Normal);
		}

        /// <summary>
        /// Disposes the region handler.
        /// </summary>
        public void Dispose() {
            NetPacketHandler.UnRegister<PlayerControls>(OnPlayerUpdate);
            NetPacketHandler.UnRegister<TileChange>(OnTileEdit);
            NetPacketHandler.UnRegister<GemLockToggle>(OnGemLockToggle);
        }

        private void OnGemLockToggle(ref ReceivePacketEvent<GemLockToggle> args) {
            var player = args.GetTSPlayer();
            var server = args.LocalReceiver.Server;
            var settings = TShock.Config.GetServerSettings(server.Name);
            if (settings.RegionProtectGemLocks) {
                if (!_regionManager.CanBuild(server, args.Packet.Position.X, args.Packet.Position.Y, player)) {
                    args.HandleMode = PacketHandleMode.Cancel;
                    args.StopPropagation = true;
                    return;
                }
            }
        }

        private void OnPlayerUpdate(ref ReceivePacketEvent<PlayerControls> args) {
            if (args.HandleMode is PacketHandleMode.Cancel)
                return;

            var player = args.GetTSPlayer();
            var pos = args.Packet.Position;
            var tileX = (int)(pos.X / 16);
            var tileY = (int)(pos.Y / 16);
            if (!player.TryBeginRegionQuery(tileX, tileY, _regionManager.Generation))
                return;

            var oldRegion = player.CurrentRegion;
            Region? next = null;
            if (_regionManager.Regions.Count != 0)
                next = _regionManager.GetTopRegionAt(args.LocalReceiver.Server.Main.worldID.ToString(), tileX, tileY);

            if (next == oldRegion)
                return;

            player.CurrentRegion = next;
            if (oldRegion != null)
                RegionHooks.OnRegionLeft(player, oldRegion);
            if (next != null)
                RegionHooks.OnRegionEntered(player, next);
        }

        private void OnTileEdit(ref ReceivePacketEvent<TileChange> args) {

            var player = args.GetTSPlayer();
            var server = args.LocalReceiver.Server;
            var worldId = server.Main.worldID.ToString();
            var pos = args.Packet.Position;

            #region Region Information Display

            if (player.AwaitingName) {
                RegionNameDisplayFlags displayFlags = player.AwaitingNameFlags;

                // If this flag is passed the final output will include unprotected regions, i.e regions
                // that have the DisableBuild flag set to false
                bool includeUnprotected = (displayFlags & RegionNameDisplayFlags.IncludeUnprotected) != 0;

                // If this flag is passed the final output will include a region's Z index
                bool includeZIndexes = (displayFlags & RegionNameDisplayFlags.IncludeZIndexes) != 0;

                // If this flag is passed the player will continue to receive region information upon editing tiles
                bool persistentMode = (displayFlags & RegionNameDisplayFlags.Persistent) != 0;

                var output = new List<string>();
                foreach (Region region in _regionManager.Regions.Where(r => r.WorldID == worldId).OrderBy(r => r.Z).Reverse()) {
                    // Ensure that the specified tile is region protected
                    if (pos.X < region.Area.Left || pos.X > region.Area.Right) {
                        continue;
                    }

                    if (pos.Y < region.Area.Top || pos.Y > region.Area.Bottom) {
                        continue;
                    }

                    // Do not include the current region if it has not been protected and the includeUnprotected flag has not been set
                    if (!region.DisableBuild && !includeUnprotected) {
                        continue;
                    }

                    output.Add($"{region.Name}{(includeZIndexes ? $" (Z:{region.Z})" : string.Empty)}");
                }

                if (output.Count == 0) {
                    player.SendInfoMessage(includeUnprotected
                        ? GetString("There are no regions at this point.")
                        : GetString("There are no regions at this point, or they are not protected."));
                }
                else {
                    player.SendInfoMessage(includeUnprotected ? GetString("Regions at this point: ") : GetString("Protected regions at this point: "));

                    foreach (string line in PaginationTools.BuildLinesFromTerms(output)) {
                        player.SendMessage(line, Color.White);
                    }
                }

                if (!persistentMode) {
                    player.AwaitingName = false;
                    player.AwaitingNameFlags = RegionNameDisplayFlags.None;
                }

                // Revert all tile changes and handle the event
                player.SendTileSquareCentered(pos.X, pos.Y, 4);
                args.HandleMode = PacketHandleMode.Cancel;
                args.StopPropagation = true;
                return;
            }

            #endregion

            #region TempPoints Setup

            if (player.AwaitingTempPoint != 0) {
                // Set temp point coordinates to current tile coordinates
                player.TempPoints[player.AwaitingTempPoint - 1].X = pos.X;
                player.TempPoints[player.AwaitingTempPoint - 1].Y = pos.Y;
                player.SendInfoMessage(GetString($"Set temp point {player.AwaitingTempPoint}."));

                // Reset the awaiting temp point
                player.AwaitingTempPoint = 0;

                // Revert all tile changes and handle the event
                player.SendTileSquareCentered(pos.X, pos.Y, 4);
                args.HandleMode = PacketHandleMode.Cancel;
                args.StopPropagation = true;
                return;
            }

            #endregion
        }
	}
}
