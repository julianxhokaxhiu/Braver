﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Braver.UI.Layout {
	internal class ItemMenu : LayoutModel {

		public Box Chars, Menu;
		public List lbItems, lbKeyItems;
        public Group Char0, Char1, Char2;

		public Label lDescription, lUse, lArrange, lKey;

		protected override void OnInit() {
			base.OnInit();
			if (Focus == null) {
				PushFocus(Menu, lUse);
			}
		}

		public override void CancelPressed() {
			if (FocusGroup == Menu) {
				_game.Audio.PlaySfx(Sfx.Cancel, 1f, 0f);
				InputEnabled = false;
				_screen.FadeOut(() => _game.PopScreen(_screen));
			} else
				base.CancelPressed();
		}

		public void MenuSelected(Label selected) {
			if (selected == lKey) {
				lbItems.Visible = false;
				lbKeyItems.Visible = true;
				if (lbKeyItems.Children.Any())
					PushFocus(lbKeyItems, lbKeyItems.Children[0]);
			} else {
                lbItems.Visible = true;
                lbKeyItems.Visible = false;
				if (selected == lUse)
					if (lbItems.Children.Any())
						PushFocus(lbItems, lbItems.Children[0]);
            }
        }

		public void SelectChar(Group selected) {
		}

		public void ItemSelected(Group selected) {
		}

		public void KeyItemFocussed() {
			var keyItem = _game.CacheItem<KeyItem>(_game.SaveData.KeyItems[lbKeyItems.GetSelectedIndex(this)]);
			lDescription.Text = keyItem.Description;
		}

    }
}
