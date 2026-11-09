# Third-party notices

Nyx is released under the GNU General Public License v3.0 (see `LICENSE`). It uses the following work.

| Component | Use | License |
|---|---|---|
| [adblock-rust](https://github.com/brave/adblock-rust) (Brave) | Filter engine, compiled into `nyx_adblock.dll` | MPL-2.0 |
| [brave/adblock-resources](https://github.com/brave/adblock-resources) | Extra scriptlet resources in `Assets/filters/resources.json` | MPL-2.0 |
| [uBlock Origin](https://github.com/gorhill/uBlock) scriptlets | Converted into `Assets/filters/resources.json` by `tools/build-resources` | GPL-3.0 |
| [EasyList and EasyPrivacy](https://easylist.to/) | Network rules in the bundled starter list (`Assets/filters/starter.txt`) and downloaded lists | GPL-3.0 or CC BY-SA 3.0 |
