# Gtag-style map test (offline)

Open this folder in Unity Hub with **Unity 6 (6000.0 LTS)** (add the Android Build Support module if you want a Quest build).

1. First open: Unity downloads the XR packages and creates `Assets/Scenes/Main.unity` by itself.
2. Press Play. The scene builds itself at runtime: map, lighting, gorilla rig, menu. Nothing to drag into the scene.
   - No headset: right-mouse-drag = look, WASD = move, Space = jump, Shift = fly, Q/E = platforms, Tab = menu (aim at a button + left click).
3. Quest build (local): menu **Gtag Test > Configure for Quest (Android)** (also enables the Oculus XR loader), then File > Build Settings > Build.
   Or headless: `Unity -batchmode -quit -projectPath . -executeMethod BuildSetup.BuildAndroid` -> `Builds/GtagMapTest.apk`.

## Unity Build Automation (Cloud Build) -> APK
1. Push this folder's *contents* to the root of a Git repo (or set "Project subdirectory" in the build target). `.gitignore` is included.
2. Create a build target: platform **Android**, Unity version **6000.0.23f1** (or the closest 6000.0 LTS offered).
3. Advanced settings -> **Pre-export method name**: `BuildSetup.PreExport`
   (creates the scene, applies IL2CPP/ARM64/Vulkan/Linear color space, assigns the Oculus loader).
4. Build. Download the APK and sideload it to the Quest (`adb install GtagMapTest.apk`).
   No keystore is needed for sideloading; add one in the target's credentials for a Store/release build.

## In VR
- Left X: open/close the **menu** - it is the ModMenuV2 "Monkey Menu Pro" prefab (`Resources/ModMenuV2/MonkeyMenuLite.prefab`), worn on your left hand and turned to face you.
  - A small blue **ball** floats in front of your right hand while the menu is open: touch a button with it to press it (green = on). Bottom buttons: **Page N** to go to the previous / next page.
  - Page 1-2: the ModMenuV2 mods (Platforms, Ghost monke, Gorrila Car, Rainbow Color, Fly, Noclip, Teleport All). Then your mods: Long Arms, Speed, Gravity, Invincible, Freeze Monster, Flashlight, Darkness, Random Spot, Save/Load Pos, Go to Stump, Reset Mods. Then Name, Color, Turn, Monster, Mirror, Mic, Reconnect, Reset Board, and the PlayFab buttons. Nothing is locked anymore.
  - Gorrila Car: right trigger drives forward, left trigger backwards. Ghost monke: others keep seeing your last pose while you move. Rainbow Color is local only. Teleport All brings every other player to you.
  - The prefab's own mod scripts (Photon / EasyInputs) were removed from the prefab copy; the buttons are driven by `MainMenu.cs`. Its URP shader-graph materials are replaced at runtime by plain colors (built-in pipeline).
  - The moderator commands (kick...) are no longer on the menu; the host-side code is still in `OnlineManager.cs`.
- **Monsters**: Timmy near the campground, the spider in the caves (see Monsters). It chases only when it sees you or you get close, and gives up a few seconds after losing you. If it catches you: "YOU DIED", back to the stump.
- **Leaderboard**: on the map's scoreboard, readable from both sides. Longest time alive, with name + color.
- **Online**: everybody joins ONE public lobby automatically ("gtag-public-lobby", up to 12 players). You see the others, with name tags, and hear them with positional voice chat. The status line at the bottom of the menu shows what is happening (Online: 3 in lobby | Voice: on).

## One-time setup for online + voice (Unity Gaming Services)
1. Open the project in the Editor once: Edit > Project Settings > Services, sign in and **link it to your Unity project**. Commit the changed ProjectSettings so Cloud Build uses the same project.
2. In the Unity Dashboard for that project, make sure **Multiplayer (Relay + Lobby)** and **Vivox** are enabled.
3. Build as usual (BuildSetup.PreExport does the rest). Quest asks for the microphone permission the first time.
If it says "Offline: ..." or "Voice: ..." at the bottom of the menu, that text is the reason.

## Security note
The owner code (123) is checked inside the app, so it keeps casual players out but is NOT real security: anyone who unpacks the APK can find it. Moderator commands are checked by the host with the same code. For real protection use a server check (e.g. Unity Cloud Code) or an allow-list of your player ID.

## Player model: the rigged gorilla (gtag-player-model-rigged.zip)
The player (and every other player online) is the rigged gorilla: 30 bones (torso, neck, head, and shoulder / upper arm / forearm / hand / fingers on both sides), skinned at runtime from `Assets/Resources/Rig/player.bytes` (made by `python Tools/pack_assets.py player gorilla.glb`; textures `player_secondary.png`, `player_face.png`).
- The **body + head** hangs under your headset and turns with it.
- The **arms are attached to the body and bend like real arms** (what other players and the mirror see): each arm hangs from its shoulder on the torso, the upper arm turns to point at the elbow, the forearm at your hand, and the hand **turns exactly like the controller** (online too: the controller rotations are sent with the pose). Two-bone IK in `MonkeSkin.PoseArmIK`; the elbow points down / out / a little back (`hint` there).
- **The arm never stretches.** Once it is fully extended it stays that long (the body is never pulled or warped) and only the **hand hitbox** - the hand sphere that does the climbing and hitting - goes on reaching to where your hand really is. Long Arms (1.5x/2x/3x) works the same way: longer reach, same arm.
- In your own view you see the headless body and just the hands, sitting exactly on the hand spheres (an arm trailing back through your face would block the view); others and the mirror see everything.
- Your color setting tints the fur (the first material).
- Finger bones are in the skeleton but are not animated yet (no finger curling).
If the hands sit on the controller the wrong way round, change `HandFix` in `MonkeModel.cs` (MonkeSkin).
If `player.bytes` is missing, the old 5-part monke.obj (`Rig/monke_mesh.txt`) is used instead.

## Hit sounds
When a hand touches something it plays a sound (louder for faster hits), picked from what that triangle of the map is made of:
`hit_concrete.mp3` (stone, concrete, ice and anything not listed), `hit_wood.mp3` (bark, tree rings, planks, shingles, the stump and its ramp), `hit_grass.mp3` (grass/moss ground, leaves, bushes, pine branches) and `hit_metal.mp3` (the corrugated and teal metal of the city, brushed / diamond-plate / rusty metal, painted posts), in `Assets/Resources/Audio`.
`hit_metal.mp3` is a clang made for this project; to use your own sound just replace that file (keep the name).
Which texture counts as wood or grass is the `WOOD_IMAGES` / `GRASS_IMAGES` list at the top of `Tools/pack_assets.py`. **Metal, ice and the stump's wood are added on top of that by `Tools/fix_map_surfaces.py`** (run it once after every `pack_assets.py map`): the city shares one texture atlas, so its metal is found by *where on the atlas* each triangle points. Code: `HandSounds.cs` (`SurfaceInfo`).

## Ice (mountains)
The dark and the light blue of the mountains (`m68` / `m64`) are slippery like ice in real Gorilla Tag: a hand on them slides along the surface instead of gripping (`SurfaceInfo.IceSlip` = 0.97 in `HandSounds.cs`; normal ground is 0.03; lower it for more grip, 1 = none). The purple tubes and the white snow are NOT ice; to add them, give their material the `ice` surface in `Tools/fix_map_surfaces.py`.

## Monsters (two of them)
- **Forest: Timmy** (`Resources/Creatures/timmy.bytes`, from timmy-red.zip). Starts around the campground, wanders the map, never enters the caves.
- **Caves: the spider** (`Resources/Creatures/spider.bytes`, from gth-spider.zip - its gorilla body mesh with your two textures). Lives in the caves and never leaves them.
Both have a real **walk animation**: they are single meshes, so a small skeleton (torso, and an upper arm + forearm per side) is built when they load, the vertices are weighted by where they are on the body and the GPU skins them (`CreatureBody.cs`). They walk hand over hand like a gorilla on its knuckles: each hand plants on the floor and slides back while the body moves over it, then lifts, swings forward and plants again, the other hand half a cycle behind; the arms bend at the elbow so a planted hand never slides; the torso dips, rises over each hand, sways onto it and leans forward while hunting. Steps follow the distance really walked, so they never glide; standing still they breathe and rock. Tune the gait with the constants at the top of `CreatureBody.cs`.
**Detection is smaller:** top of `Monster.cs` - Timmy `ForestSightRange` 20 m (was 45) / `ForestCloseRange` 6 m (was 12), spider 14 m / 5 m. Close range no longer works from another floor.
Timmy/Freeze/Monster on-off in the menu apply to both. Models: `Tools/pack_assets.py timmy|spider`.

## Mirror in the hat room
The mirror stands in the hat room (the white busts), exactly where the map keeps its own mirror (`mirror_fixed`), against the wall and facing into the room, so it is out of the way of the exits. It replaces the map's own mirror, which has no picture in Unity. It is a real mirror (a second camera at your reflected eye, so it follows your head) and it is where you see your whole monke body. It only renders when you are within 12 m and not facing away. Settings > **Mirror** turns it off completely if you need the frames back (Quest).

## Changes from the Quest test video
1. Monsters walk (see Monsters). 2. City metal plays the metal sound. 3. The stump and the ramp down from it play wood (they used the ground's moss texture, so they sounded like grass). 4. Dark and light blue in the mountains are slippery. 5. Player arms are attached and bend; 6. a fully stretched arm stays the same length and only the hand hitbox extends.

## Fixed: pink/magenta mirror frame (and platforms)
The mirror's wooden frame, the grip platforms and the simple-shape fallback bodies were made from Unity primitives, whose default shader is left out of Android builds, so they drew magenta on the headset. They now use `MonkeModel.NewMaterial` (Legacy Diffuse, always in builds): `StumpMirror.Box`, `MainMenu.HandPlatform`, `Monster.Part`, `PlayerRigModel.Part`, `RemoteAvatar.Part`.

## Fixed: movement after dying to the monster
Two causes, both fixed:
1. `Player.cs` wiped its swing history on every teleport but not the running average of it, so the leftover "ghost" speed could never be subtracted again and launched you the next time a hand touched something. `InitializeValues()` now clears all of it (also fixes Random Spot, Load Pos, Long Arms and noclip changes).
2. You could still swing, fly and slide during the YOU DIED screen. `Player.locked` now freezes you from the moment you are caught until you are back at the stump and fully reset.

## Player rig (your Blender model)
Unity Cloud has no Blender, so convert once on a PC with Blender:
`blender -b _Originals/Gorilla_IK_Rig.blend -P Tools/export_rig_fbx.py` -> writes `Assets/Resources/Rig/GorillaRig.fbx`.
The game loads that FBX automatically. Until it exists, a built-in gorilla (body + stretchy arms) is used.


## New map (the_entire_gtag_map.glb) and size
The whole map (Sketchfab "The Entire GTAG Map", by Frogggy, CC-BY-4.0 - keep the credit) is packed into `Assets/Resources/Map/map.bytes` (500k triangles, textures shrunk to 512 px in `Map/Textures`) by `Tools/pack_assets.py map the_entire_gtag_map.glb`. No importer or extra package is needed. The whole project zip is about 10 MB.
- You **start in the cosmetics room** next to the in-map computer. The leaderboard is on the forest scoreboard (campground). You never start in the forest.
- Sky sphere and far scenery are visual only (no collision). Leaves/bushes with transparency are drawn unlit.
- Chunks named `cave_*` are the caves, `forest_*` the forest; the monsters use those names to stay in their area.
- The old FBX map was removed.

## Files
- `Assets/Scripts/GorillaLocomotion/` - your Player.cs + Surface.cs (Player.cs has three small additions: `handMoveScale`, `locked` and the state reset)
- `Assets/Scripts/GtagBootstrap.cs` - builds the scene, loads the map, spawns the rig with the original prefab's tuning values
- `Assets/Editor/BuildSetup.cs` - Quest/XR build configuration used by the menu, the batch build and Cloud Build
- `Assets/Scripts/XRRigInput.cs` - headset/controller input (+ desktop fallback)
- `Assets/Scripts/MainMenu.cs` - combined main menu (mods + settings + player)
- `Tools/fix_map_surfaces.py` - adds metal / ice / stump-wood surfaces to `Resources/Map/map.bytes` (run after `pack_assets.py map`)
- `Assets/Scripts/OnlineManager.cs`, `RemoteAvatar.cs` - public lobby, voice, other players, moderator commands
- `Assets/Scripts/Monster.cs`, `Leaderboard.cs`, `PlayerRigModel.cs`, `PlayerProfile.cs` - monster behaviour, board, your body, saved settings
- `Assets/Scripts/MonkeModel.cs` (mesh loader + arm skinning, `MonkeSkin`), `CorruptedMonke.cs` (monster body + hand-walking), `StumpMirror.cs` (mirror)
- `Tools/export_rig_fbx.py` - Blender -> FBX export for the rig
- `Assets/Resources/Map/` - map FBX + textures (30 textures in your zip; the FBX references a few more that weren't included, those materials stay plain)
- `_Originals/` - your original unitypackage (outside Assets, ignored by Unity)

Poison_Menu.dll is intentionally not in the project: it hooks into the real game's code and can't load in a standalone Unity project.

## PlayFab (no SDK needed)
`Assets/Scripts/PlayFabLite.cs` talks to the PlayFab REST API directly. Put your **Title ID** into `PlayFabLite.TitleId` (top of the file). It signs in with a device ID, sets your display name, and sends your best survival time to a statistic called `BestSurvivalSeconds` (create it in the PlayFab dashboard under Leaderboards).

## Menu font (works on Unity Cloud with no extra step)
`Assets/Resources/ModMenuV2/` holds the prefab and its TextMeshPro font (Daydream). Every text in the menu is forced onto that font, and `MainMenu.Clean` turns any character the font does not have into a space (it has letters, digits, space and `! " ' , . / ?`). If TextMeshPro's "TMP Settings" asset is missing (a fresh Cloud Build), `MainMenu.EnsureTmpSettings` creates it in memory, so no Essential Resources import or committed `Assets/TextMesh Pro` folder is needed. `Assets/Editor/TmpSetup.cs` still imports the essentials when you open the project in the Editor, which is optional.
