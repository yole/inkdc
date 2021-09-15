VAR gold = 0
VAR health = 20
~ alter(gold, 7)
~ alter(health, -4)
== function alter(ref x, k) ==
~ x = x + k
