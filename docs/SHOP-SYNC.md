# Johnny shop synchronization

The in-world `StoreRoomFunction` shop uses host stock, prices, discounts and a shared team YTP wallet. Native host awards fund that wallet; matching client awards are ignored to avoid counting shared activities twice. Inventory remains personal. Native sticker packs and glue rewards go to the buyer, while a purchased map flag is mirrored to the team.

A remote purchase quotes the displayed item and price; a changed quote is rejected before funds are charged. Clients wait for the current level's host shop snapshot before interacting. A purchase reserves the shelf item and debits the host wallet before a grant is sent. The buyer checks inventory space again before collecting it and acknowledges the delivery. A failed delivery refunds the exact reserved price and returns stock. Repeated requests cannot buy reserved or sold stock. A lost acknowledgement cannot safely prove failure, so an uncertain delivery remains spent and sold. Restocking waits for pending purchases, then runs Johnny's native restock once on the host and distributes the resulting stock and prices.

Price labels, SOLD labels, descriptions and native Johnny purchase bookkeeping follow host state. Johnny's shortfall assistance is replaced with an explicit insufficient-team-funds rejection. Shop purchases require an empty inventory slot rather than swapping a held item onto the shelf.

The old alternate `StoreScreen` interface is outside this implementation. General client-only YTP effects that have no synchronized host event are not forwarded to the team wallet. Shop visitors/idle dialogue and sticker reward animations remain local.

## Focused verification

`RunShopFocusedTest()` runs only with both `-bt-lab` and `-bt-focused-sync`. It uses a generated real shop and normal network requests: reject a purchase with an empty wallet, seed an isolated lab wallet, fill the buyer inventory before a grant to verify the refund, clear space and deliver one purchase, then reject a duplicate shelf request. Successful diagnostics have host `shopTestStep=4`, client `shopTestStep=6`, one host purchase, one host refund, at least two host rejections, no reservations, and both wallet balances equal to `10000 - client.shopTestCost`. Lab-only inventory and wallet changes must never run in a normal game.
