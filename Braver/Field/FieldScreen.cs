﻿// This program and the accompanying materials are made available under the terms of the
//  Eclipse Public License v2.0 which accompanies this distribution, and is available at
//  https://www.eclipse.org/legal/epl-v20.html
//  
//  SPDX-License-Identifier: EPL-2.0

using Braver.Plugins;
using Braver.Plugins.Field;
using Ficedula.FF7.Field;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Braver.Field {

    public class BattleOptions {
        public string OverrideMusic { get; set; }
        public string PostBattleMusic { get; set; } //will play in field
        public bool BattlesEnabled { get; set; } = true; //TODO - reasonable default?
        public Battle.BattleFlags Flags { get; set; } = Battle.BattleFlags.None;
    }

    public class FieldInfo {
        public float OriginalBGZFrom { get; set; }
        public float OriginalBGZTo { get; set; }
        public float BGZFrom { get; set; }
        public float BGZTo { get; set; }
    }

    public class FieldLine {
        public Vector3 P0 { get; set; }
        public Vector3 P1 { get; set; }
        public bool Active { get; set; } = true;

        public bool IntersectsWith(FieldModel m, float intersectDistance) {
            if (!Active) return false;
            return m.IntersectsLine(P0, P1, intersectDistance);
        }
    }

    public class FieldScreen : Screen, Net.IListen<Net.FieldModelMessage>, Net.IListen<Net.FieldBGMessage>,
        Net.IListen<Net.FieldEntityModelMessage>, Net.IListen<Net.FieldBGScrollMessage>,
        IField {

        private PerspView3D _view3D;
        private View2D _view2D;
        private FieldDebug _debug;
        private FieldInfo _info;
        private string _file;

        private bool _debugMode = false;
        private bool _renderBG = true, _renderDebug = false, _renderModels = true;
        private float _controlRotation;
        private bool _renderUI = true;

        private string _debugEntity = "ba";

        private PluginInstances<IFieldLocation> _fieldPlugins;
        private PluginInstances<IBackground> _bgPlugins;
        private PluginInstances<IDialog> _dialogPlugins;
        private PluginInstances<IMovie> _moviePlugins;

        private List<WalkmeshTriangle> _walkmesh;

        public override Color ClearColor => Color.Black;

        public Entity Player { get; private set; }

        public override string Description => "Location " + Game.SaveData.Location;

        public Action WhenPlayerSet { get; set; }

        public HashSet<int> DisabledWalkmeshTriangles { get; } = new HashSet<int>();
        public int WalkmeshTriCount => _walkmesh.Count;
        public Background Background { get; private set; }
        public Movie Movie { get; private set; }
        public List<Entity> Entities { get; private set; }
        public List<FieldModel> FieldModels { get; private set; }
        public DialogEvent FieldDialog { get; private set; }
        public TriggersAndGateways TriggersAndGateways { get; private set; }
        public Shake ShakeEffect { get; private set; }

        private class Focusable {
            public string Name { get; set; }
            public Func<Vector3> Position { get; set; }
            public Func<int> WalkmeshTri { get; set; }
            public Func<bool> Active { get; set; }
            public object Source { get; set; }
        }

        private List<Focusable> _focusables = new();
        private Focusable _currentFocus;


        private EncounterTable[] _encounters;
        public FieldOptions Options { get; set; } = FieldOptions.DEFAULT;
        public Dialog Dialog { get; private set; }
        public FieldUI FieldUI { get; private set; }
        public Overlay Overlay { get; private set; }
        public IInputCapture InputCapture { get; set; }

        public int BattleTable { get; set; }
        public BattleOptions BattleOptions { get; } = new();

        private HashSet<Trigger> _activeTriggers = new();

        private FieldDestination _destination;
        private short _fieldID;
        public FieldScreen(FieldDestination destination) {
            _destination = destination;
            _fieldID = destination.DestinationFieldID;
        }

        private void SetPlayerIfNecessary() {
            if (Player == null) {
                var autoPlayer = Entities
                    .Where(e => e.Character != null)
                    .FirstOrDefault(e => e.Character == Game.SaveData.Party.OrderBy(c => c.CharIndex).FirstOrDefault());
                if (autoPlayer != null)
                    SetPlayer(Entities.IndexOf(autoPlayer));
            }
        }

        private PerspView3D ViewFromCamera(CameraMatrix cam) {
            if (cam == null) return null;

            double fovy = (2 * Math.Atan(240.0 / (2.0 * cam.Zoom))) * 57.29577951;
            //This produces a FOV that's about 7% higher than FF7 (PC at least) uses - but that's because
            //FF7 PC doesn't use the full screen height, it has black bars that reduce the usable height
            //by around 7% - so this gives correct results for the resolution we want to render at.

            var camPosition = cam.CameraPosition.ToX() * 4096f;
            /*
            var camDistances = _walkmesh
                .SelectMany(tri => new[] { tri.V0.ToX(), tri.V1.ToX(), tri.V2.ToX() })
                .Select(v => (camPosition - v).Length());

            float nearest = camDistances.Min(), furthest = camDistances.Max();
            */

            //Seems like FF7 uses near/far clipping planes of 50/32000 on most (all?!?) field locations

            return new PerspView3D {
                FOV = (float)fovy,
                ZNear = 50, //nearest * 0.75f,
                ZFar = 32000, //furthest,// * 1.25f,
                CameraPosition = camPosition,
                CameraForwards = cam.Forwards.ToX(),
                CameraUp = cam.Up.ToX(),
            };
        }

        private static bool _isFirstLoad = true;

        public override void Init(FGame g, GraphicsDevice graphics) {
            base.Init(g, graphics);

            UpdateSaveLocation();
            if (g.GameOptions.AutoSaveOnFieldEntry && !_isFirstLoad)
                Game.AutoSave();
            _isFirstLoad = false;

            g.Net.Listen<Net.FieldModelMessage>(this);
            g.Net.Listen<Net.FieldBGMessage>(this);
            g.Net.Listen<Net.FieldEntityModelMessage>(this);
            g.Net.Listen<Net.FieldBGScrollMessage>(this);

            g.Audio.StopLoopingSfx(true);

            Overlay = new Overlay(g, graphics);

            g.Net.Send(new Net.FieldScreenMessage { Destination = _destination });

            FieldFile field;

            var mapList = g.Singleton(() => new MapList(g.Open("field", "maplist")));
            _file = mapList.Items[_destination.DestinationFieldID];
            var cached = g.Singleton(() => new CachedField());
            if (cached.FieldID == _destination.DestinationFieldID)
                field = cached.FieldFile;
            else {
                using (var s = g.Open("field", _file))
                    field = new FieldFile(s);
            }

            _fieldPlugins = GetPlugins<IFieldLocation>(_file);
            _dialogPlugins = GetPlugins<IDialog>(_file);
            _bgPlugins = GetPlugins<IBackground>(_file);
            _moviePlugins = GetPlugins<IMovie>(_file);

            _fieldPlugins.Call(f => f.Init(this));

            Background = new Background(g, _bgPlugins, graphics, field.GetBackground());
            Movie = new Movie(g, graphics, _moviePlugins);
            FieldDialog = field.GetDialogEvent();
            _encounters = field.GetEncounterTables().ToArray();

            ShakeEffect = new Shake();

            Entities = FieldDialog.Entities
                .Select(e => new Entity(e, this))
                .ToList();

            FieldModels = field.GetModels()
                .Models
                .Select((m, index) => {
                    var model = new FieldModel(
                        graphics, g, index, m.HRC,
                        m.Animations.Select(s => System.IO.Path.ChangeExtension(s, ".a")),
                        globalLightColour: m.GlobalLightColor,
                        light1Colour: m.Light1Color, light1Pos: m.Light1Pos.ToX(),
                        light2Colour: m.Light2Color, light2Pos: m.Light2Pos.ToX(),
                        light3Colour: m.Light3Color, light3Pos: m.Light3Pos.ToX()
                    ) {
                        Scale = float.Parse(m.Scale) / 128f,
                        Rotation2 = new Vector3(0, 0, 0),
                    };
                    model.Translation2 = new Vector3(
                        0,
                        0,
                        model.Scale * model.MaxBounds.Y
                    );
                    return model;
                })
                .ToList();

            TriggersAndGateways = field.GetTriggersAndGateways();
            _controlRotation = 360f * TriggersAndGateways.ControlDirection / 256f;

            _walkmesh = field.GetWalkmesh().Triangles;

            using (var sinfo = g.TryOpen("field", _file + ".xml")) {
                if (sinfo != null) {
                    _info = Serialisation.Deserialise<FieldInfo>(sinfo);
                } else
                    _info = new FieldInfo();
            }

            var cam = field.GetCameraMatrices().First();

            /*
            float camWidth, camHeight;
            if (_info.Cameras.Any()) {
                camWidth = _info.Cameras[0].Width;
                camHeight = _info.Cameras[0].Height;
                _base3DOffset = new Vector2(_info.Cameras[0].CenterX, _info.Cameras[0].CenterY);
            } else {
                //Autodetect...
                var testCam = new OrthoView3D {
                    CameraPosition = new Vector3(cam.CameraPosition.X, cam.CameraPosition.Z, cam.CameraPosition.Y),
                    CameraForwards = new Vector3(cam.Forwards.X, cam.Forwards.Z, cam.Forwards.Y),
                    CameraUp = new Vector3(cam.Up.X, cam.Up.Z, cam.Up.Y),
                    Width = 1280,
                    Height = 720,
                };
                var vp = testCam.View * testCam.Projection;

                Vector3 Project(FieldVertex v) {
                    return Vector3.Transform(new Vector3(v.X, v.Y, v.Z), vp);
                }

                Vector3 vMin, vMax;
                vMin = vMax = Project(_walkmesh[0].V0);

                foreach(var wTri in _walkmesh) {
                    Vector3 v0 = Vector3.Transform(new Vector3(wTri.V0.X, wTri.V0.Y, wTri.V0.Z), vp),
                        v1 = Vector3.Transform(new Vector3(wTri.V1.X, wTri.V1.Y, wTri.V1.Z), vp),
                        v2 = Vector3.Transform(new Vector3(wTri.V2.X, wTri.V2.Y, wTri.V2.Z), vp);
                    vMin = new Vector3(
                        Math.Min(vMin.X, Math.Min(Math.Min(v0.X, v1.X), v2.X)),
                        Math.Min(vMin.Y, Math.Min(Math.Min(v0.Y, v1.Y), v2.Y)),
                        Math.Min(vMin.Z, Math.Min(Math.Min(v0.Z, v1.Z), v2.Z))
                    );
                    vMax = new Vector3(
                        Math.Max(vMax.X, Math.Max(Math.Max(v0.X, v1.X), v2.X)),
                        Math.Max(vMax.Y, Math.Max(Math.Max(v0.Y, v1.Y), v2.Y)),
                        Math.Max(vMax.Z, Math.Max(Math.Max(v0.Z, v1.Z), v2.Z))
                    );
                }

                var allW = _walkmesh.SelectMany(t => new[] { t.V0, t.V1, t.V2 });
                Vector3 wMin = new Vector3(allW.Min(v => v.X), allW.Min(v => v.Y), allW.Min(v => v.Z)),
                    wMax = new Vector3(allW.Max(v => v.X), allW.Max(v => v.Y), allW.Max(v => v.Z)); 

                float xRange = (vMax.X - vMin.X) * 0.5f,
                    yRange = (vMax.Y - vMin.Y) * 0.5f;

                //So now we know the walkmap would cover xRange screens across and yRange screens down
                //Compare that to the background width/height and scale it to match...

                System.Diagnostics.Trace.WriteLine($"Walkmap range {wMin} - {wMax}");
                System.Diagnostics.Trace.WriteLine($"Transformed {vMin} - {vMax}");
                System.Diagnostics.Trace.WriteLine($"Walkmap covers range {xRange}/{yRange}");
                System.Diagnostics.Trace.WriteLine($"Background is size {Background.Width} x {Background.Height}");
                System.Diagnostics.Trace.WriteLine($"Background covers {Background.Width / 320f} x {Background.Height / 240f} screens");
                System.Diagnostics.Trace.WriteLine($"...or in widescreen, {Background.Width / 427f} x {Background.Height / 240f} screens");

                camWidth = 1280f * xRange / (Background.Width / 320f);
                camHeight = 720f * yRange / (Background.Height / 240f);
                System.Diagnostics.Trace.WriteLine($"Auto calculated ortho w/h to {camWidth}/{camHeight}");

                camWidth = 1280f * xRange / (Background.Width / 427f);
                camHeight = 720f * yRange / (Background.Height / 240f);
                System.Diagnostics.Trace.WriteLine($"...or in widescreen, {camWidth}/{camHeight}");

                _base3DOffset = Vector2.Zero;
            }

            _view3D = new OrthoView3D {
                CameraPosition = new Vector3(cam.CameraPosition.X, cam.CameraPosition.Z, cam.CameraPosition.Y),
                CameraForwards = new Vector3(cam.Forwards.X, cam.Forwards.Z, cam.Forwards.Y),
                CameraUp = new Vector3(cam.Up.X, cam.Up.Z, cam.Up.Y),
                Width = camWidth,
                Height = camHeight,
                CenterX = _base3DOffset.X,
                CenterY = _base3DOffset.Y,
            };
            */

            _view3D = ViewFromCamera(cam);

            var vp = System.Numerics.Matrix4x4.CreateLookAt(
                cam.CameraPosition * 4096f, cam.CameraPosition * 4096f + cam.Forwards, cam.Up
            ) * System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView(
                (float)(_view3D.FOV * Math.PI / 180.0), 1280f / 720f, 0.001f * 4096f, 1000f * 4096f
            );

            float minZ = 1f, maxZ = 0f;
            foreach (var wTri in _walkmesh) {
                System.Numerics.Vector4 v0 = System.Numerics.Vector4.Transform(new System.Numerics.Vector4(wTri.V0.X, wTri.V0.Y, wTri.V0.Z, 1), vp),
                    v1 = System.Numerics.Vector4.Transform(new System.Numerics.Vector4(wTri.V1.X, wTri.V1.Y, wTri.V1.Z, 1), vp),
                    v2 = System.Numerics.Vector4.Transform(new System.Numerics.Vector4(wTri.V2.X, wTri.V2.Y, wTri.V2.Z, 1), vp);
                /*
                System.Diagnostics.Trace.WriteLine(v0 / v0.W);
                System.Diagnostics.Trace.WriteLine(v1 / v1.W);
                System.Diagnostics.Trace.WriteLine(v2 / v2.W);
                */
                minZ = Math.Min(minZ, v0.Z / v0.W);
                minZ = Math.Min(minZ, v1.Z / v1.W);
                minZ = Math.Min(minZ, v2.Z / v2.W);
                maxZ = Math.Max(maxZ, v0.Z / v0.W);
                maxZ = Math.Max(maxZ, v1.Z / v1.W);
                maxZ = Math.Max(maxZ, v2.Z / v2.W);
            }
            System.Diagnostics.Trace.WriteLine($"Walkmesh Z varies from {minZ}-{maxZ} (recip {1f / minZ} to {1f / maxZ}");
            _debug = new FieldDebug(graphics, field);

            Dialog = new Dialog(g, _dialogPlugins, graphics);
            FieldUI = new FieldUI(g, graphics);

            g.Memory.ResetScratch();

            _view2D = new View2D {
                Width = 1280,
                Height = 720,
                ZNear = 0,
                ZFar = -1,
            };

            if (g.Net is Net.Server) {
                if (!Game.GameOptions.NoFieldScripts) {
                    foreach (var entity in Entities) {
                        entity.Call(7, 0, null);
                        entity.Run(9999, true);
                    }
                }
                SetPlayerIfNecessary(); //TODO - is it OK to delay doing this? But until the entity scripts run we don't know which entity corresponds to which party member...

                var scroll = GetBGScroll();
                if ((scroll.x == 0) && (scroll.y == 0)) //don't bring player into view if script appears to have scrolled away
                    BringPlayerIntoView();

                if (!Overlay.HasTriggered)
                    Overlay.Fade(30, GraphicsUtil.BlendSubtractive, Color.White, Color.Black, null);

                g.Net.Send(new Net.ScreenReadyMessage());
            }
            Entity.DEBUG_OUT = false;

            foreach(var entity in Entities.Where(e => e.Model != null)) {
                _focusables.Add(new Focusable {
                    Name = entity.Name,
                    Position = () => entity.Model.Translation,
                    WalkmeshTri = () => entity.WalkmeshTri,
                    Source = entity,
                    Active = () => entity.Model.Visible && (entity != Player)
                });
            }
            foreach(var gateway in TriggersAndGateways.Gateways) {
                var middle = (gateway.V0.ToX() + gateway.V1.ToX()) * 0.5f;
                var tri = FindWalkmeshForPosition(middle);
                if (tri != null) {
                    _focusables.Add(new Focusable {
                        Name = "Exit " + TriggersAndGateways.Gateways.IndexOf(gateway),
                        Position = () => middle,
                        WalkmeshTri = () => tri.Value,
                        Source = gateway,
                        Active = () => true,
                    });
                }
            }
        }

        private int _nextModelIndex = 0;
        public int GetNextModelIndex() {
            return _nextModelIndex++;
        }

        protected override void DoRender() {
            //System.Diagnostics.Trace.WriteLine($"FieldScreen:Render");
            Graphics.DepthStencilState = DepthStencilState.Default;
            Graphics.BlendState = BlendState.AlphaBlend;

            PerspView3D viewer3D = null;
            if (Options.HasFlag(FieldOptions.UseMovieCam) && Movie.Active)
                viewer3D = ViewFromCamera(Movie.Camera)?.Clone();
            viewer3D ??= _view3D.Clone();

            var view2D = _view2D.Clone();
            ShakeEffect.Apply(view2D, viewer3D);

            if (_renderBG) {
                //Render non-transparent background (or movie, if it's active)
                if (Movie.Active)
                    Movie.Render();
                else
                    Background.Render(view2D, false);
            }

            if (_renderDebug)
                _debug.Render(viewer3D);

            if (_renderModels) {
                using (var state = new GraphicsState(Graphics, rasterizerState: RasterizerState.CullClockwise)) {
                    foreach (int pass in Enumerable.Range(1, 2))
                        foreach (var entity in Entities)
                            if ((entity.Model != null) && entity.Model.Visible)
                                entity.Model.Render(viewer3D, pass == 2);
                }
            }

            //Now render blend layers over actual background + models
            if (_renderBG && !Movie.Active)
                Background.Render(view2D, true);

            Overlay.Render();

            if (_renderUI && !Movie.Active && Options.HasFlag(FieldOptions.PlayerControls))
                FieldUI.Render();
            Dialog.Render();
        }

        private class FrameProcess {
            public int Frames;
            public Func<int, bool> Process;
            public string Name;
        }

        private List<FrameProcess> _processes = new();

        /// <summary>
        /// Registers a process which will be called back every frame until complete. The callback should
        /// return true when it is complete, and will then not be called again. It's passed the number of
        /// frames that have elapsed.
        /// </summary>
        /// <param name="process">Callback to run each frame</param>
        /// <param name="name">
        /// Unique name for process. If non-null, this process will replace any
        /// existing callback with the same name.
        /// </param>
        public void StartProcess(Func<int, bool> process, string name = null) {
            if (name != null)
                _processes.RemoveAll(proc => proc.Name == name);
            _processes.Add(new FrameProcess { Process = process, Name = name });
        }

        private int _frame = 0;
        protected override void DoStep(GameTime elapsed) {
            if (Game.Net is Net.Server) {
                if ((_frame % 2) == 0) {
                    Overlay.Step();
                    ShakeEffect.Step();
                    foreach (var entity in Entities) {
                        if (!Game.GameOptions.NoFieldScripts && !Options.HasFlag(FieldOptions.NoScripts))
                            entity.Run(8);
                        entity.Model?.FrameStep();
                    }
                }

                for (int i = _processes.Count - 1; i >= 0; i--) {
                    var process = _processes[i];
                    if (process.Process(process.Frames++))
                        _processes.RemoveAt(i);
                }

                FieldUI.Step(this);
                Dialog.Step();
                Movie.Step();
                Background.Step();
            } else {
                if ((_frame % 2) == 0) {
                    foreach (var entity in Entities)
                        entity.Model?.FrameStep();
                }
            }
            _fieldPlugins.Call(loc => loc.Step());
            _frame++;
        }

        public (int x, int y) GetBGScroll() {
            return (
                (int)(-_view2D.CenterX / 3),
                (int)(_view2D.CenterY / 3)
            );
        }
        public void BGScroll(float x, float y) {
            BGScrollOffset(x - (-_view2D.CenterX / 3), y - (_view2D.CenterY / 3));
        }
        public void BGScrollOffset(float ox, float oy) {

            _view2D.CenterX -= 3 * ox;
            _view2D.CenterY += 3 * oy;

            var newScroll = GetBGScroll();
            _view3D.ScreenOffset = new Vector2(newScroll.x * 3f * 2 / 1280, newScroll.y * -3f * 2 / 720);

            Game.Net.Send(new Net.FieldBGScrollMessage {
                X = _view2D.CenterX / 3,
                Y = _view2D.CenterY / 3,
            });
        }

        private void ReportAllModelPositions() {
            foreach (var entity in Entities.Where(e => e.Model != null)) {
                System.Diagnostics.Trace.WriteLine($"Entity {entity.Name} at pos {entity.Model.Translation}, 2D background pos {ModelToBGPosition(entity.Model.Translation)}");
            }
        }

        public (int x, int y) ClampBGScrollToViewport(int x, int y) {
            var result = ClampBGScrollToViewport(new Vector2(x, y));
            return ((int)result.X, (int)result.Y);
        }
        public Vector2 ClampBGScrollToViewport(Vector2 bgScroll) {
            int minX, maxX, minY, maxY;

            if (Background.Width < (1280f / 3))
                minX = maxX = 0;
            else {
                minX = Background.MinX + (1280 / 3) / 2;
                maxX = (Background.MinX + Background.Width) - (1280 / 3) / 2;
            }

            if (Background.Height < (720f / 3))
                minY = maxY = 0;
            else {
                minY = Background.MinY + (720 / 3) / 2;
                maxY = (Background.MinY + Background.Height) - (730 / 3) / 2;
            }

            return new Vector2(
                Math.Min(Math.Max(minX, bgScroll.X), maxX),
                Math.Min(Math.Max(minY, bgScroll.Y), maxY)
            );
        }

        public void BringPlayerIntoView() {
            if (Options.HasFlag(FieldOptions.CameraIsAsyncScrolling)) return;

            if (Player != null) {
                var posOnBG = ModelToBGPosition(Player.Model.Translation);
                float playerHeight = (Player.Model.MaxBounds.Y - Player.Model.MinBounds.Y) * Player.Model.Scale;
                var highPosOnBG = ModelToBGPosition(Player.Model.Translation + new Vector3(0, 0, playerHeight));
                var scroll = GetBGScroll();
                var newScroll = scroll;
                if (posOnBG.X > (scroll.x + 50))
                    newScroll.x = (int)posOnBG.X - 50;
                else if (posOnBG.X < (scroll.x - 50))
                    newScroll.x = (int)posOnBG.X + 50;

                if (highPosOnBG.Y > (scroll.y + 45))
                    newScroll.y = (int)highPosOnBG.Y - 45;
                else if (posOnBG.Y < (scroll.y - 45))
                    newScroll.y = (int)posOnBG.Y + 45;

                newScroll = ClampBGScrollToViewport(newScroll.x, newScroll.y);

                if (newScroll != scroll) {
                    System.Diagnostics.Trace.WriteLine($"BringPlayerIntoView: Player at BG pos {posOnBG}, BG scroll is {scroll}, needs to be {newScroll}");
                    BGScroll(newScroll.x, newScroll.y);
                }
            }
        }

        public Vector2 ModelToBGPosition(Vector3 modelPosition, Matrix? transformMatrix = null, bool debug = false) {
            transformMatrix ??= _view3D.View * _view3D.Projection;
            var screenPos = Vector4.Transform(modelPosition, transformMatrix.Value);
            screenPos = screenPos / screenPos.W;

            float tx = (_view2D.CenterX / 3) + screenPos.X * 0.5f * (1280f / 3),
                  ty = (_view2D.CenterY / 3) + screenPos.Y * 0.5f * (720f / 3);

            if (debug)
                System.Diagnostics.Trace.WriteLine($"ModelToBG: {modelPosition} -> screen {screenPos} -> BG {tx}/{ty}");

            return new Vector2(-tx, ty);
        }

        private InputState _lastInput;

        internal InputState LastInput => _lastInput;

        private void UpdateSaveLocation() {
            Game.SaveData.Module = Module.Field;
            Game.SaveData.FieldDestination = _destination ?? new FieldDestination {
                Triangle = (ushort)Player.WalkmeshTri,
                X = (short)Player.Model.Translation.X,
                Y = (short)Player.Model.Translation.Y,
                Orientation = (byte)(Player.Model.Rotation.Y * 255 / 360),
                DestinationFieldID = _fieldID,
            };
        }

        [Flags]
        private enum LineEvents {
            None = 0,
            OK = 0x1,
            Move = 0x2,
            MoveClose = 0x4,
            Go = 0x8,
            GoOnce = 0x10,
            GoAway = 0x20,
        }

        private static List<(LineEvents, int)> _lineEventByPriority = new List<(LineEvents, int)> {
            (LineEvents.GoOnce, 5),
            (LineEvents.Go, 4),
            (LineEvents.Move, 2),
            (LineEvents.MoveClose, 3),
            (LineEvents.OK, 1),
            (LineEvents.GoAway, 6),
        };

        private void SwitchFocus(int offset) {
            _currentFocusState = null;
            foreach (int i in Enumerable.Range(1, _focusables.Count - 1)) {
                int newIndex = (_focusables.IndexOf(_currentFocus) + offset * i + _focusables.Count) % _focusables.Count;
                var candidate = _focusables[newIndex];
                if (candidate.Active()) {
                    _currentFocus = candidate;
                    return;
                }
            }
            _currentFocus = null;
        }

        public override void ProcessInput(InputState input) {
            base.ProcessInput(input);
            if (!(Game.Net is Net.Server)) return;

            _lastInput = input;
            if (input.IsJustDown(InputKey.Start))
                _debugMode = !_debugMode;

            if (input.IsJustDown(InputKey.Select))
                _renderUI = !_renderUI;

            if (input.IsJustDown(InputKey.Debug1))
                _renderBG = !_renderBG;
            if (input.IsJustDown(InputKey.Debug2))
                _renderDebug = !_renderDebug;
            if (input.IsJustDown(InputKey.Debug3)) {
                _renderModels = !_renderModels;
                ReportAllModelPositions();
            }

            if (input.IsJustDown(InputKey.PanLeft))
                SwitchFocus(-1);
            if (input.IsJustDown(InputKey.PanRight))
                SwitchFocus(+1);

            if (input.IsJustDown(InputKey.Debug5))
                Game.PushScreen(new UI.Layout.LayoutScreen("FieldDebugger", parm: this));

            if (input.IsDown(InputKey.Debug4)) {
                /*
                if (input.IsDown(InputKey.Up))
                    _bgZFrom++;
                else if (input.IsDown(InputKey.Down))
                    _bgZFrom--;

                if (input.IsDown(InputKey.Left))
                    _bgZTo--;
                else if (input.IsDown(InputKey.Right))
                    _bgZTo++;

                if (input.IsDown(InputKey.Start)) {
                    using (var s = Game.WriteDebugBData("field", _file + ".xml")) {
                        var info = new FieldInfo {
                            BGZFrom = _bgZFrom, BGZTo = _bgZTo,
                            OriginalBGZFrom = Background.AutoDetectZFrom, OriginalBGZTo = Background.AutoDetectZTo,
                        };
                        Serialisation.Serialise(info, s);
                    }
                }

                System.Diagnostics.Trace.WriteLine($"BGZFrom {_bgZFrom} ZTo {_bgZTo}");
                return;
                */
            }

            if (_debugMode) {

                if (input.IsDown(InputKey.PanLeft))
                    BGScrollOffset(0, -1);
                else if (input.IsDown(InputKey.PanRight))
                    BGScrollOffset(0, +1);

                if (input.IsDown(InputKey.Up))
                    _view3D.CameraPosition += _view3D.CameraUp;
                if (input.IsDown(InputKey.Down))
                    _view3D.CameraPosition -= _view3D.CameraUp;

            } else {

                if (Dialog.IsActive) {
                    Dialog.ProcessInput(input);
                    return;
                }

                if (InputCapture != null) {
                    InputCapture.ProcessInput(input);
                    return;
                }

                if (input.IsJustDown(InputKey.Menu) && Options.HasFlag(FieldOptions.MenuEnabled)) {
                    UpdateSaveLocation();
                    Game.PushScreen(new UI.Layout.LayoutScreen("MainMenu"));
                    return;
                }

                //Normal controls
                if ((Player != null) && Options.HasFlag(FieldOptions.PlayerControls)) {

                    Dictionary<Entity, LineEvents> lineEvents = new();
                    void SetLineEvent(Entity e, LineEvents events) {
                        lineEvents.TryGetValue(e, out LineEvents current);
                        lineEvents[e] = current | events;
                    }

                    if (input.IsJustDown(InputKey.OK) && (Player != null)) {
                        var talkTo = Player.CanTalkWith
                            .Where(e => e.Flags.HasFlag(EntityFlags.CanTalk)) //Check just in case flag was turned off after we moved next to them
                            .FirstOrDefault();
                        if (talkTo != null) {
                            if (!talkTo.Call(1, 1, null))
                                System.Diagnostics.Trace.WriteLine($"Could not start talk script for entity {talkTo}");
                        }
                    }

                    int desiredAnim = 0;
                    float animSpeed = 1f;
                    if (input.IsAnyDirectionDown()) {
                        //TODO actual use controldirection
                        var forwards = Vector3.Transform(_view3D.CameraForwards.WithZ(0), Matrix.CreateRotationZ((_controlRotation + 180) * (float)Math.PI / 180f));
                        forwards.Normalize();
                        var right = Vector3.Transform(forwards, Matrix.CreateRotationZ(90f * (float)Math.PI / 180f));
                        var move = Vector2.Zero;

                        if (input.IsDown(InputKey.Up))
                            move += new Vector2(forwards.X, forwards.Y);
                        else if (input.IsDown(InputKey.Down))
                            move -= new Vector2(forwards.X, forwards.Y);

                        if (input.IsDown(InputKey.Left))
                            move += new Vector2(right.X, right.Y);
                        else if (input.IsDown(InputKey.Right))
                            move -= new Vector2(right.X, right.Y);

                        if (move != Vector2.Zero) {
                            move.Normalize();
                            move *= 3;
                            if (input.IsDown(InputKey.Cancel)) {
                                animSpeed = 2f;
                                move *= 4f;
                                desiredAnim = 2;
                            } else
                                desiredAnim = 1;

                            TryWalk(Player, Player.Model.Translation + new Vector3(move.X, move.Y, 0), true);
                            Player.Model.Rotation = Player.Model.Rotation.WithZ((float)(Math.Atan2(move.X, -move.Y) * 180f / Math.PI));

                            var oldLines = Player.LinesCollidingWith.ToArray();
                            var oldGateways = Player.GatewaysCollidingWidth.ToArray();
                            PlayerRepositioned();

                            foreach (var entered in Player.LinesCollidingWith.Except(oldLines))
                                SetLineEvent(entered, LineEvents.GoOnce);

                            foreach (var left in oldLines.Except(Player.LinesCollidingWith))
                                SetLineEvent(left, LineEvents.GoAway);

                            if (Options.HasFlag(FieldOptions.GatewaysEnabled)) {
                                foreach (var gateway in Player.GatewaysCollidingWidth.Except(oldGateways)) {
                                    Options &= ~FieldOptions.PlayerControls;
                                    desiredAnim = 0; //stop player walking as they won't move any more!
                                    FadeOut(() => {
                                        Game.ChangeScreen(this, new FieldScreen(gateway.Destination));
                                    });
                                }
                            }

                            foreach (var trigger in TriggersAndGateways.Triggers) {
                                bool active = GraphicsUtil.LineCircleIntersect(trigger.V0.ToX().XY(), trigger.V1.ToX().XY(), Player.Model.Translation.XY(), Player.CollideDistance);
                                if (active != _activeTriggers.Contains(trigger)) {

                                    bool setOn = false, setOff = false;
                                    switch (trigger.Behaviour) {
                                        case TriggerBehaviour.OnNone:
                                            if (active)
                                                setOn = true;
                                            break;
                                        case TriggerBehaviour.OffNone:
                                            if (active)
                                                setOff = true;
                                            break;
                                        case TriggerBehaviour.OnOff:
                                        case TriggerBehaviour.OnOffPlus: //TODO - plus side only
                                            setOn = active;
                                            setOff = !active;
                                            break;
                                        case TriggerBehaviour.OffOn:
                                        case TriggerBehaviour.OffOnPlus: //TODO - plus side only
                                            setOn = !active;
                                            setOff = active;
                                            break;
                                        default:
                                            throw new NotImplementedException();
                                    }

                                    if (setOn)
                                        Background.ModifyParameter(trigger.BackgroundID, i => i | (1 << trigger.BackgroundState));
                                    if (setOff)
                                        Background.ModifyParameter(trigger.BackgroundID, i => i & ~(1 << trigger.BackgroundState));

                                    if ((setOn || setOff) && (trigger.SoundID != 0))
                                        Game.Audio.PlaySfx(trigger.SoundID - 1, 1f, 0f);

                                    if (active)
                                        _activeTriggers.Add(trigger);
                                    else
                                        _activeTriggers.Remove(trigger);
                                }
                            }

                            if (Options.HasFlag(FieldOptions.CameraTracksPlayer))
                                BringPlayerIntoView();

                            if ((_frame % 20) == 0) {
                                Game.SaveData.FieldDangerCounter += (int)(1024 * animSpeed * animSpeed / _encounters[BattleTable].Rate);
                                if (_r.Next(256) < (Game.SaveData.FieldDangerCounter / 256)) {
                                    System.Diagnostics.Trace.WriteLine($"FieldDangerCounter: trigger encounter and reset");
                                    Game.SaveData.FieldDangerCounter = 0;
                                    if (BattleOptions.BattlesEnabled && _encounters[BattleTable].Enabled && !Game.GameOptions.NoRandomBattles) {
                                        Battle.BattleScreen.Launch(Game, _encounters[BattleTable], BattleOptions.Flags, _r);
                                    }
                                }
                            }

                        } else {
                            //
                        }

                        foreach (var inLine in Player.LinesCollidingWith) {
                            SetLineEvent(inLine, LineEvents.Move);
                            if (inLine.Line.IntersectsWith(Player.Model, Player.CollideDistance / 4))
                                SetLineEvent(inLine, LineEvents.MoveClose);
                            if (input.IsJustDown(InputKey.OK))
                                SetLineEvent(inLine, LineEvents.OK);
                        }

                    }

                    foreach (var inLine in Player.LinesCollidingWith)
                        SetLineEvent(inLine, LineEvents.Go);

                    foreach(var line in lineEvents.Keys) {
                        foreach(var evt in _lineEventByPriority) {
                            if (lineEvents[line].HasFlag(evt.Item1) && line.ScriptExists(evt.Item2)) {
                                if (!line.Call(1, evt.Item2, null)) //TODO - priority??
                                    System.Diagnostics.Trace.WriteLine($"Line {line.Name} couldn't call script");
                                break;
                            }
                        }
                    }

                    if ((Player.Model.AnimationState.Animation != desiredAnim) || (Player.Model.AnimationState.AnimationSpeed != animSpeed))
                        Player.Model.PlayAnimation(desiredAnim, true, animSpeed);

                }
            }

        }

        private static bool LineIntersect(Vector2 a0, Vector2 a1, Vector2 b0, Vector2 b1, out float aDist) {
            double denominator = ((a1.X - a0.X) * (b1.Y - b0.Y)) - ((a1.Y - a0.Y) * (b1.X - b0.X));
            double numerator1 = 1.0 * ((a0.Y - b0.Y) * (b1.X - b0.X)) - 1.0 * ((a0.X - b0.X) * (b1.Y - b0.Y));
            double numerator2 = 1.0 * ((a0.Y - b0.Y) * (a1.X - a0.X)) - 1.0 * ((a0.X - b0.X) * (a1.Y - a0.Y));


            if (denominator == 0) {
                aDist = 0;
                return numerator1 == 0 && numerator2 == 0;
            }

            aDist = (float)Math.Round(numerator1 / denominator, 2);
            double s = Math.Round(numerator2 / denominator, 2);

            return (aDist >= 0 && aDist <= 1) && (s >= 0 && s <= 1);
        }

        private bool CalculateTriLeave(Vector2 startPos, Vector2 endPos, WalkmeshTriangle tri, out float dist, out short? newTri, out Vector2 tv0, out Vector2 tv1) {
            tv0 = tri.V0.ToX().XY();
            tv1 = tri.V1.ToX().XY();
            if (LineIntersect(startPos, endPos, tv0, tv1, out dist)) {
                newTri = tri.V01Tri;
                return true;
            }

            tv0 = tri.V1.ToX().XY();
            tv1 = tri.V2.ToX().XY();
            if (LineIntersect(startPos, endPos, tv0, tv1, out dist)) {
                newTri = tri.V12Tri;
                return true;
            }

            tv0 = tri.V2.ToX().XY();
            tv1 = tri.V0.ToX().XY();
            if (LineIntersect(startPos, endPos, tv0, tv1, out dist)) {
                newTri = tri.V20Tri;
                return true;
            }

            newTri = null;
            return false;
        }

        private enum LeaveTriResult {
            Failure,
            Success,
            SlideCurrentTri,
            SlideNewTri,
        }

        private void FindOtherVerts(WalkmeshTriangle tri, FieldVertex v, out FieldVertex v1, out FieldVertex v2) {
            if (tri.V0 == v) {
                v1 = tri.V1;
                v2 = tri.V2;
            } else if (tri.V1 == v) {
                v1 = tri.V0;
                v2 = tri.V2;
            } else if (tri.V2 == v) {
                v1 = tri.V0;
                v2 = tri.V1;
            } else
                throw new NotImplementedException();
        }

        private void FindAdjacentTris(WalkmeshTriangle tri, FieldVertex v, out short? t0, out short? t1) {
            if (tri.V0 == v) {
                t0 = tri.V01Tri;
                t1 = tri.V20Tri;
            } else if (tri.V1 == v) {
                t0 = tri.V01Tri;
                t1 = tri.V12Tri;
            } else if (tri.V2 == v) {
                t0 = tri.V12Tri;
                t1 = tri.V20Tri;
            } else
                throw new NotImplementedException();
        }

        private double AngleBetweenVectors(Vector2 v0, Vector2 v1) {
            double angle = Math.Atan2(v0.Y, v0.X) - Math.Atan2(v1.Y, v1.X);
            while (angle > Math.PI) angle -= 2 * Math.PI;
            while (angle <= -Math.PI) angle += 2 * Math.PI;
            return angle;
        }

        private static Random _r = new();

        private static float MAX_SLIDE_ANGLE = (float)(70f * Math.PI / 180f);

        private LeaveTriResult DoesLeaveTri(Vector2 startPos, Vector2 endPos, int currentTri, bool allowSlide, out short? newTri, out Vector2 newDestination) {
            newDestination = Vector2.Zero;

            var tri = _walkmesh[currentTri];

            var origDir = (endPos - startPos);
            var origDistance = origDir.Length();
            origDir.Normalize();

            //Now see if we're exactly on a vert. If so, find ALL the tris which join that vert.
            //We'll try and shift into one of them and then when the move is retried, we'll hopefully make some progress... :/

            foreach (var vert in tri.AllVerts()) {
                if ((vert.X == (short)startPos.X) && (vert.Y == (short)startPos.Y)) {

                    var candidates = _walkmesh
                        .SelectMany((t, index) => t.AllVerts()
                            .Where(v => v != vert)
                            .Select(otherV => {
                                var dir = otherV.ToX().XY() - vert.ToX().XY();
                                dir.Normalize();
                                return new {
                                    Tri = t,
                                    TIndex = index,
                                    VStart = vert.ToX().XY(),
                                    VEnd = otherV.ToX().XY(),
                                    Angle = AngleBetweenVectors(dir, origDir)
                                };
                            })
                        )
                        .Where(a => !DisabledWalkmeshTriangles.Contains(a.TIndex))
                        .Where(a => a.Tri.AllVerts().Any(v => v == vert))
                        .OrderBy(a => Math.Abs(a.Angle));

                    if (candidates.Any()) {
                        var choice = candidates.First();
                        if (choice.Tri != tri) {
                            newDestination = choice.VStart;
                            newTri = (short)choice.TIndex;
                            return LeaveTriResult.SlideNewTri;
                        } else {
                            var edge = choice.VEnd - choice.VStart;
                            var distance = edge.Length();
                            edge.Normalize();
                            if (distance < origDistance)
                                newDestination = choice.VEnd;
                            else
                                newDestination = startPos + edge * origDistance;
                            newTri = null;
                            return LeaveTriResult.SlideCurrentTri;
                        }
                    }
                }
            }

            
            newTri = null;

            var vector = endPos - startPos;

            Dictionary<int, int> considered = new(); // TriIndex -> Crossing count
            HashSet<int> toConsider = new HashSet<int> { currentTri };
           
            while (toConsider.Any()) {
                var check = toConsider.ToArray();
                toConsider.Clear();
                foreach(int t in check) {
                    var checkTri = _walkmesh[t];
                    int intersections = 0;

                    void Check(FieldVertex vA, FieldVertex vB, short? nextTri) {

                        if (LineIntersect(startPos, endPos, vA.ToX().XY(), vB.ToX().XY(), out _)) {
                            intersections++;

                            if (nextTri == null) return;
                            if (DisabledWalkmeshTriangles.Contains(nextTri.Value)) return;
                            if (considered.ContainsKey(nextTri.Value)) return;

                            toConsider.Add(nextTri.Value);
                        }
                    }

                    Check(checkTri.V0, checkTri.V1, checkTri.V01Tri);
                    Check(checkTri.V0, checkTri.V2, checkTri.V20Tri);
                    Check(checkTri.V1, checkTri.V2, checkTri.V12Tri);
                    considered[t] = intersections;
                }
            }

            var inTris = considered
                .Where(kv => kv.Value == 1)
                .Select(kv => kv.Key)
                .Where(t => t != currentTri)
                .ToArray();

            switch (inTris.Length) {
                case 0:
                    break; //OK - we don't end up in any reachable tri
                case 1:
                    //OK - we end up in a new tri, woo
                    newTri = (short)inTris[0];
                    newDestination = endPos;
                    return LeaveTriResult.Success;
                default:
                    //We're exactly on the boundary between multiple triangles. Gonna have to pick one...
                    System.Diagnostics.Trace.WriteLine($"DoesLeaveTri: multiple inTris, picking one");
                    newTri = (short)inTris[0];
                    newDestination = endPos;
                    return LeaveTriResult.Success;
            }

            if (!CalculateTriLeave(startPos, endPos, tri, out _, out _, out var tv0, out var tv1))
                return LeaveTriResult.Failure;

            if (allowSlide) {

                //If we get here, we're not exactly on one of the current tri's verts, but may be able
                //to slide along an edge to end up closer to our desired end point.
                //Calculate angles from end-start-v0 and end-start-v1 to find which vert we can slide towards
                //while minimising the change in direction from our original heading.
                //Only slide if the edge is < 70 degrees off our original heading as it's weird otherwise!

                var v0dir = (tv0 - startPos);
                var v0Distance = v0dir.Length();
                v0dir.Normalize();

                var v1dir = (tv1 - startPos);
                var v1Distance = v1dir.Length();
                v1dir.Normalize();

                double v0angle = AngleBetweenVectors(v0dir, origDir),
                    v1angle = AngleBetweenVectors(v1dir, origDir);

                if ((Math.Abs(v0angle) < Math.Abs(v1angle)) && (Math.Abs(v0angle) < MAX_SLIDE_ANGLE)) {
                    //Try to slide towards v0
                    if (v0Distance < origDistance)
                        newDestination = tv0;
                    else
                        newDestination = startPos + v0dir * origDistance;
                    return LeaveTriResult.SlideCurrentTri;
                } else if (Math.Abs(v1angle) < MAX_SLIDE_ANGLE) {
                    //Try to slide towards v1
                    if (v1Distance < origDistance)
                        newDestination = tv1;
                    else
                        newDestination = startPos + v1dir * origDistance;
                    return LeaveTriResult.SlideCurrentTri;
                }

            }

            return LeaveTriResult.Failure;
        }

        public bool TryWalk(Entity eMove, Vector3 newPosition, bool doCollide) {

            void PopulateNearby(EntityFlags flag, Func<Entity, float> getDistance, Func<Entity, HashSet<Entity>> getMatching, string description) {
                var thisMatch = getMatching(eMove);
                thisMatch.Clear();
                Entities.ForEach(e => getMatching(e).Remove(eMove));

                var toCheck = Entities
                    .Where(e => e.Flags.HasFlag(flag))
                    .Where(e => e.Model != null)
                    .Where(e => e != eMove);

                foreach (var entity in toCheck) {
                    if (entity.Model != null) {
                        var dist = (entity.Model.Translation.XY() - newPosition.XY()).Length();
                        var collision = getDistance(eMove) + getDistance(entity);
                        if (dist <= collision) {
                            System.Diagnostics.Trace.WriteLine($"Entity {eMove} is now in {description} distance with {entity}");
                            thisMatch.Add(entity);
                            getMatching(entity).Add(eMove);
                        }
                    }
                }
            }

            PopulateNearby(EntityFlags.CanTalk, e => e.TalkDistance, e => e.CanTalkWith, "talk");
            if (doCollide) {
                PopulateNearby(EntityFlags.CanCollide, e => e.CollideDistance, e => e.CollidingWith, "collide");
                //Now check if, for any of the models we're colliding with, we're not moving 
                //clearly further away. If so, don't allow the move.
                foreach(var other in eMove.CollidingWith) {
                    //Use law of cosines to work out the angle between our current position to the other 
                    //object, and our current position to the new position. If the angle is less than 90,
                    //then we're heading towards the new object while colliding with it, so disallow the move.
                    //We don't just compare distances and allow any movement that ends up further away from
                    //the colliding object, because then we can potentially teleport through a small enough
                    //object!
                    float a = (other.Model.Translation - eMove.Model.Translation).Length(),
                        b = (newPosition - eMove.Model.Translation).Length(),
                        c = (other.Model.Translation - newPosition).Length();
                    double C = Math.Acos((a * a + b * b - c * c) / (2 * a * b));

                    if (C < (Math.PI / 2))
                        return false;
                }
            }

            var oldPosition = eMove.Model.Translation;
            void ReportMove() {
                _fieldPlugins.Call(loc => loc.EntityMoved(eMove, false, oldPosition, eMove.Model.Translation)); //TODO running
            }

            var currentTri = _walkmesh[eMove.WalkmeshTri];
            var newHeight = HeightInTriangle(currentTri, newPosition.X, newPosition.Y, false);
            if (newHeight != null) {
                //We're staying in the same tri, so just update height

                if (!CalculateTriLeave(newPosition.XY(), new Vector2(9999, 9999), currentTri, out _, out _, out _, out _))
                    throw new Exception($"Sanity check failed");

                eMove.Model.Translation = newPosition.WithZ(newHeight.Value);
                ReportDebugEntityPos(eMove);
                ReportMove();
                return true;
            } else {
                switch (DoesLeaveTri(eMove.Model.Translation.XY(), newPosition.XY(), eMove.WalkmeshTri, true, out short? newTri, out Vector2 newDest)) {
                    case LeaveTriResult.Failure:
                        return false;
                    case LeaveTriResult.SlideCurrentTri:
                        ClampToTriangle(ref newDest, currentTri);
                        break;
                    case LeaveTriResult.Success:
                    case LeaveTriResult.SlideNewTri:
                        eMove.WalkmeshTri = newTri.Value;
                        _currentFocusState = null; //Could be a bit cleverer about when to invalidate this
                        currentTri = _walkmesh[newTri.Value];
                        ClampToTriangle(ref newDest, currentTri);
                        break; //Treat same as success, code below will move us accordingly
                    default:
                        throw new NotImplementedException();
                }

                newHeight = HeightInTriangle(currentTri, newDest.X, newDest.Y, true);
                if (newHeight == null)
                    throw new Exception();
                eMove.Model.Translation = new Vector3(newDest.X, newDest.Y, newHeight.Value);
                ReportDebugEntityPos(eMove);
                ReportMove();
                return true;
            }
        }

        private void ClampToTriangle(ref Vector2 position, WalkmeshTriangle tri) {
            CalculateBarycentric(tri.V0.ToX(), tri.V1.ToX(), tri.V2.ToX(), position,
                out float a, out float b, out float c);

            a = Math.Min(Math.Max(a, 0f), 1f);
            b = Math.Min(Math.Max(b, 0f), 1f);
            c = Math.Min(Math.Max(c, 0f), 1f);

            float norm = a + b + c;
            a = a / norm;
            b = b / norm;
            c = c / norm;

            position = new Vector2(
                tri.V0.X * a + tri.V1.X * b + tri.V2.X * c,
                tri.V0.Y * a + tri.V1.Y * b + tri.V2.Y * c
            );
        }

        public float? HeightInTriangle(int triID, float x, float y, bool allowClampAndRound) {
            return HeightInTriangle(_walkmesh[triID], x, y, allowClampAndRound);
        }
        private static float? HeightInTriangle(WalkmeshTriangle tri, float x, float y, bool allowClampAndRound) {
            return HeightInTriangle(tri.V0.ToX(), tri.V1.ToX(), tri.V2.ToX(), x, y, allowClampAndRound);
        }
        private static float? HeightInTriangle(Vector3 p0, Vector3 p1, Vector3 p2, float x, float y, bool allowClampAndRound) {

            CalculateBarycentric(p0, p1, p2, new Vector2(x, y), out var a, out var b, out var c);

            if (allowClampAndRound) {
                //For height specifically, when we've already determined this is definitely the tri we're inside,
                //allow being *slightly* outside the triangle due to floating point imprecision
                a = (float)Math.Round(a, 2);
                b = (float)Math.Round(b, 2);
                c = (float)Math.Round(c, 2);
            }

            if (a < 0) return null;
            if (b < 0) return null;
            if (c < 0) return null;
            if (a > 1) return null;
            if (b > 1) return null;
            if (c > 1) return null;

            return (float)(p0.Z * a + p1.Z * b + p2.Z * c);
        }

        private static void CalculateBarycentric(Vector3 va, Vector3 vb, Vector3 vc, Vector2 pos, out float a, out float b, out float c) {
            double denominator = (vb.Y - vc.Y) * (va.X - vc.X) + (vc.X - vb.X) * (va.Y - vc.Y);
            
            a = (float)(((vb.Y - vc.Y) * (pos.X - vc.X) + (vc.X - vb.X) * (pos.Y - vc.Y)) / denominator);
            b = (float)(((vc.Y - va.Y) * (pos.X - vc.X) + (va.X - vc.X) * (pos.Y - vc.Y)) / denominator);

            c = 1 - a - b;
        }

        private void ReportDebugEntityPos(Entity e) {
            if (e.Name == _debugEntity) {
                System.Diagnostics.Trace.WriteLine($"Ent {e.Name} at pos {e.Model.Translation} wmtri {e.WalkmeshTri}");
                var tri = _walkmesh[e.WalkmeshTri];
                CalculateBarycentric(tri.V0.ToX(), tri.V1.ToX(), tri.V2.ToX(), e.Model.Translation.XY(), out var a, out var b, out var c);
                System.Diagnostics.Trace.WriteLine($"---Barycentric pos {a} / {b} / {c}");
            }
        }

        public int? FindWalkmeshForPosition(Vector3 position) {
            foreach(int t in Enumerable.Range(0, _walkmesh.Count)) {
                var height = HeightInTriangle(t, position.X, position.Y, false);
                if (height != null) {
                    if (((height.Value - 5) <= position.Z) && (height.Value > (position.Z - 5))) //TODO - is this fudge factor needed, the right size, ...?
                        return t;
                }
            }

            return null;
        }

        public void DropToWalkmesh(Entity e, Vector2 position, int walkmeshTri, bool exceptOnFailure = true) {
            var tri = _walkmesh[walkmeshTri];

            var height = HeightInTriangle(tri, position.X, position.Y, true);

            if ((height == null) && exceptOnFailure)
                throw new Exception($"Cannot DropToWalkmesh - position {position} does not have a height in walkmesh tri {walkmeshTri}");

            e.Model.Translation = new Vector3(position.X, position.Y, height.GetValueOrDefault());
            e.WalkmeshTri = walkmeshTri;
            _currentFocusState = null; //Could be a bit cleverer about when to invalidate this
            ReportDebugEntityPos(e);
        }

        public void PlayerRepositioned() {
            //Applying a minimum distance of 20 on the player's collision radius
            //e.g. in mds7pb_1 Cloud has a zero collision radius, but it's expecting us to trigger a 
            //line event.
            Player.LinesCollidingWith.Clear();
            foreach (var lineEnt in Entities.Where(e => e.Line != null)) {
                if (lineEnt.Line.IntersectsWith(Player.Model, Math.Max(Player.CollideDistance, 20)))
                    Player.LinesCollidingWith.Add(lineEnt);
            }
            Player.GatewaysCollidingWidth.Clear();
            foreach (var gateway in TriggersAndGateways.Gateways)
                if (Player.Model.IntersectsLine(gateway.V0.ToX(), gateway.V1.ToX(), Math.Max(Player.CollideDistance, 20)))
                    Player.GatewaysCollidingWidth.Add(gateway);
        }

        public void CheckPendingPlayerSetup() {
            if ((_destination != null) && (Player.Model != null) && (_destination.Triangle != ushort.MaxValue)) {
                DropToWalkmesh(Player, new Vector2(_destination.X, _destination.Y), _destination.Triangle, false);
                Player.Model.Rotation = new Vector3(0, 0, 360f * _destination.Orientation / 255f);
                //This is necessary because the field scripts will position the player inside lines
                //and gateways, and expect events to not trigger (because the player isn't "entering" the
                //line/gateway) - until they leave and then re-enter later.
                PlayerRepositioned();
                _destination = null;
            }
        }

        public void SetPlayer(int whichEntity) {
            Player = Entities[whichEntity]; //TODO: also center screen etc.

            if (Player.CollideDistance == Entity.DEFAULT_COLLIDE_DISTANCE)
                Player.CollideDistance = Entity.DEFAULT_PLAYER_COLLIDE_DISTANCE;

            CheckPendingPlayerSetup();
            WhenPlayerSet?.Invoke();
        }

        public void SetPlayerControls(bool enabled) {
            if (enabled)
                Options |= FieldOptions.PlayerControls | FieldOptions.CameraTracksPlayer; //Seems like cameratracksplayer MUST be turned on now or things break...?
            else {
                Options &= ~FieldOptions.PlayerControls;
                if (Player?.Model != null)
                    Player.Model.PlayAnimation(0, true, 1f);
                //TODO - is this reasonable? Disable current (walking) animation when we take control away from the player? 
                //(We don't want e.g. walk animation to be continuing after our control is disabled and we're not moving any more!)
            }
        }

        public void TriggerBattle(int which) {
            Battle.BattleScreen.Launch(Game, which, BattleOptions.Flags);
        }

        public override void Suspended() {
            base.Suspended();
            _fieldPlugins.Call(f => f.Suspended());
        }

        public void Received(Net.FieldModelMessage message) {
            var model = FieldModels[message.ModelID];
            if (message.Visible.HasValue)
                model.Visible = message.Visible.Value;
            if (message.Translation.HasValue)
                model.Translation = message.Translation.Value;
            if (message.Translation2.HasValue)
                model.Translation2 = message.Translation2.Value;
            if (message.Rotation.HasValue)
                model.Rotation = message.Rotation.Value;
            if (message.Rotation2.HasValue)
                model.Rotation2 = message.Rotation2.Value;
            if (message.Scale.HasValue)
                model.Scale = message.Scale.Value;
            if (message.AnimationState != null)
                model.AnimationState = message.AnimationState;
            if (message.AmbientLightColour != null)
                model.AmbientLightColour = message.AmbientLightColour.Value;
            if (message.ShineEffect != null)
                model.ShineEffect = message.ShineEffect.Value;
            if (message.EyeAnimation != null)
                model.EyeAnimation = message.EyeAnimation.Value;
            if (message.GlobalAnimationSpeed != null)
                model.GlobalAnimationSpeed = message.GlobalAnimationSpeed.Value;
        }

        public void Received(Net.FieldBGMessage message) {
            Background.SetParameter(message.Parm, message.Value);
        }
        public void Received(Net.FieldBGScrollMessage message) {
            BGScroll(message.X, message.Y);
        }

        public void Received(Net.FieldEntityModelMessage message) {
            Entities[message.EntityID].Model = FieldModels[message.ModelID];
        }

        private FocusState _currentFocusState;

        int IField.FieldID => _fieldID;
        string IField.FieldFile => _file;
        IReadOnlyList<WalkmeshTriangle> IField.Walkmesh => _walkmesh.AsReadOnly();
        Vector3 IField.PlayerPosition => Player?.Model?.Translation ?? Vector3.Zero;
        Vector2 IField.Transform(Vector3 position) => _view3D.ProjectTo2D(position).XY();
        FocusState IField.GetFocusState() {
            if ((_currentFocus != null) && (_currentFocusState == null) && (Player != null) && (_currentFocus.Source != Player)) {

                Dictionary<WalkmeshTriangle, int> calculated = new();
                var playerTri = _walkmesh[Player.WalkmeshTri];
                calculated[playerTri] = 0;
                Queue<WalkmeshTriangle> toConsider = new Queue<WalkmeshTriangle>();
                toConsider.Enqueue(_walkmesh[Player.WalkmeshTri]);

                while (toConsider.Any()) {
                    var tri = toConsider.Dequeue();
                    foreach(var adjacent in tri.AdjacentTris()) {
                        var adj = _walkmesh[adjacent];
                        if (!calculated.ContainsKey(adj)) {
                            calculated[adj] = calculated[tri] + 1;
                            toConsider.Enqueue(adj);
                        }
                    }
                }

                _currentFocusState = new FocusState {
                    TargetName = _currentFocus.Name,
                    TargetPosition = _currentFocus.Position(),
                    WalkmeshDistance = calculated[_walkmesh[_currentFocus.WalkmeshTri()]],
                    WalkmeshTriPoints = new List<int>(),
                };
                var last = _currentFocus.WalkmeshTri();
                foreach (int d in Enumerable.Range(0, _currentFocusState.WalkmeshDistance).Reverse()) {
                    var current = _walkmesh[last];
                    _currentFocusState.WalkmeshTriPoints.Add(last);
                    var next = current.AdjacentTris()
                        .Where(t => calculated[_walkmesh[t]] == d)
                        .First();
                    last = next;
                }
            }
            return _currentFocusState;
        }
    }

    public class CachedField {
        public int FieldID { get; set; } = -1;
        public FieldFile FieldFile { get; set; }

        public void Load(FGame g, int fieldID) {
            var mapList = g.Singleton(() => new MapList(g.Open("field", "maplist")));
            string file = mapList.Items[fieldID];
            using (var s = g.Open("field", file))
                FieldFile = new FieldFile(s);
            FieldID = fieldID;
        }
    }

    public interface IInputCapture {
        void ProcessInput(InputState input);
    }
}
