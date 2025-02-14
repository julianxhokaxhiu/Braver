﻿// This program and the accompanying materials are made available under the terms of the
//  Eclipse Public License v2.0 which accompanies this distribution, and is available at
//  https://www.eclipse.org/legal/epl-v20.html
//  
//  SPDX-License-Identifier: EPL-2.0

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Permissions;
using System.Text;
using System.Threading.Tasks;

namespace Braver {
    internal class TestScreen : Screen {

        public override string Description => "Test Screen";
        public override Color ClearColor => Color.AliceBlue;

        private Field.FieldModel _model;
        private PerspView3D _viewer;
        private int _anim;
        private string[] _anims = new[] { "ACFE.a" };

        public override void Init(FGame g, GraphicsDevice graphics) {
            base.Init(g, graphics);

            graphics.BlendState = BlendState.AlphaBlend;

            _model = new Field.FieldModel(graphics, g, 0, "AXDC.hrc", _anims, "field");
            _model.PlayAnimation(_anim, true, 1f);
            _model.Scale = 1f;
            _model.Rotation2 = new Vector3(0, 0, 180);

            _viewer = new PerspView3D {
                CameraPosition = new Vector3(0, 50f, 10f),
                CameraForwards = new Vector3(0, -50f, -5f),
                CameraUp = Vector3.UnitZ,                
            };
        }

        public override void ProcessInput(InputState input) {
            base.ProcessInput(input);
            if (input.IsJustDown(InputKey.OK)) {
                _anim = (_anim + 1) % _anims.Length;
                System.Diagnostics.Trace.WriteLine($"Anim: {_anims[_anim]}");
                _model.PlayAnimation(_anim, true, 1f);
            }
            if (input.IsDown(InputKey.Up)) {
                _viewer.CameraPosition += _viewer.CameraUp;
            }
            if (input.IsDown(InputKey.Down)) {
                _viewer.CameraPosition -= _viewer.CameraUp;
            }
        }

        protected override void DoRender() {
            Graphics.DepthStencilState = DepthStencilState.Default;
            Graphics.RasterizerState = RasterizerState.CullClockwise;
            _model.Render(_viewer, false);
            _model.Render(_viewer, true);
        }

        private float _z = 5;
        private int _frame;
        protected override void DoStep(GameTime elapsed) {
            //_model.Rotation = new Vector3(0, 0, 90);
            //_model.Translation = new Vector3(0, 0, _z);
            if ((_frame++ % 4) == 0)
                _model.FrameStep();
        }
    }
}
