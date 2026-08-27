"""
The location tables.

This Feature was written last in its phase and named as the risky one for two
years, on the grounds that it "changes the shape of data other Features already
own". The design here is the answer to that, and it is worth stating before any
of the tables:

**It changes nobody's shape. It gives a code a meaning.**

`advanced-inventory` has stamped a `location` on every stock movement since its
1.0, and `restaurant-operations` on every ticket, table and slot since its. Both
of those columns were added before this Feature existed, both default to the
empty string, and both were documented at the time as the column this Feature
would name. That was not foresight for its own sake: a Feature owns only its own
tables, so `multi-location` *cannot* add a column to a movement or a ticket, and
adding one afterwards would have meant a migration over every row a merchant had
ever recorded — which is precisely the migration risk everybody was afraid of.

What follows from that is the property that makes this deliverable at all:

- installing it **migrates nothing**. Every existing row already carries the code
  it belongs to, which for a single-site merchant is `""`;
- uninstalling it **loses no operational data**. The stamps stay exactly where
  they are and merely stop having names, addresses and opening hours attached;
- and a store can adopt it **gradually**, because a code nobody has described
  here is still a perfectly good code — it is just an anonymous one.

The second decision worth stating: **a route is decided once and written down.**
Where an order is handled is a fact about that order, not a function of the rules
that happen to exist today. A route recomputed on read would move last Tuesday's
order to a different branch the moment somebody edited a rule, and the branch
that actually cooked it would disappear from every report.
"""

from django.db import models

#: The code a single-site merchant's rows already carry, and the one every other
#: Feature defaults to. Named here so that "the location that means no location"
#: is a constant rather than a bare `""` in a comparison.
DEFAULT_LOCATION = ""


class LocationKind(models.TextChoices):
    """
    What kind of place this is.

    It exists because routing needs it: an order can be collected from a shop and
    cannot be collected from a warehouse, and a dark store is a kitchen with no
    door for customers. A single free-text "type" would make that judgement
    unaskable.
    """

    SHOP = "shop", "A place customers come to"
    KITCHEN = "kitchen", "A place food is made"
    WAREHOUSE = "warehouse", "A place stock is kept"
    DARK_STORE = "dark-store", "A place that only fulfils, with no counter"


class Location(models.Model):
    """
    One branch, kitchen or warehouse.

    `code` is the identity and it is the same string other Features have been
    stamping on their rows all along — that is the entire integration. Nothing
    here holds a foreign key into another Feature's tables, and nothing there
    holds one into these; the join is a string both sides already agreed on.
    """

    code = models.CharField(max_length=40, unique=True)
    name = models.CharField(max_length=200)
    kind = models.CharField(max_length=20, choices=LocationKind, default=LocationKind.SHOP)

    #: Its own, and not the store's. A merchant with a branch in another timezone
    #: has opening hours that mean different moments in each, and a single
    #: store-wide timezone would make one of the two wrong twice a year.
    timezone = models.CharField(max_length=64, blank=True, default="")

    address_line1 = models.CharField(max_length=250, blank=True, default="")
    address_line2 = models.CharField(max_length=250, blank=True, default="")
    city = models.CharField(max_length=120, blank=True, default="")
    postal_code = models.CharField(max_length=32, blank=True, default="")
    latitude = models.FloatField(null=True, blank=True)
    longitude = models.FloatField(null=True, blank=True)

    phone = models.CharField(max_length=40, blank=True, default="")
    email = models.EmailField(blank=True, default="")

    #: Where an order goes when no rule matched. Exactly one location may hold
    #: this, and the database is what says so — two defaults is a routing table
    #: with a coin toss in it.
    is_default = models.BooleanField(default=False)

    #: A branch that is closed for refurbishment, or one a merchant has not
    #: opened yet. Inactive rather than deleted: every stock movement and every
    #: ticket ever stamped with its code still refers to it, and deleting the row
    #: would turn all of that history anonymous again.
    is_active = models.BooleanField(default=True)

    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        db_table = "knight_locations_location"
        ordering = ("code",)
        indexes = [
            models.Index(fields=["is_active", "kind"], name="knight_loc_active_kind"),
        ]
        constraints = [
            models.UniqueConstraint(
                fields=["is_default"],
                condition=models.Q(is_default=True),
                name="knight_loc_one_default",
            ),
        ]

    def __str__(self) -> str:
        return f"{self.code} ({self.name})"


class OpeningHours(models.Model):
    """
    When a location is open, one row per window per weekday.

    Rows rather than a "09:00-17:00" string, because a shop that closes for lunch
    has two windows on a Tuesday and a parser is not the place to discover that.
    Weekday is Python's numbering — Monday is 0 — stated here because a schema
    that leaves it implicit is a schema somebody reads as Sunday-first.
    """

    location = models.ForeignKey(Location, on_delete=models.CASCADE, related_name="hours")
    weekday = models.PositiveSmallIntegerField()
    opens = models.TimeField()
    closes = models.TimeField()

    class Meta:
        db_table = "knight_locations_hours"
        ordering = ("weekday", "opens")
        constraints = [
            models.UniqueConstraint(
                fields=["location", "weekday", "opens"],
                name="knight_loc_one_window_per_start",
            ),
            models.CheckConstraint(
                condition=models.Q(weekday__lte=6),
                name="knight_loc_weekday_is_a_weekday",
            ),
            # A window that ends before it starts is either a typo or a shop that
            # trades past midnight, and the two need different rows. Refused
            # rather than guessed at: guessing wrong closes a bar at nine in the
            # morning.
            models.CheckConstraint(
                condition=models.Q(closes__gt=models.F("opens")),
                name="knight_loc_window_ends_after_it_starts",
            ),
        ]

    def __str__(self) -> str:
        return f"{self.location_id} {self.weekday} {self.opens}-{self.closes}"


class Closure(models.Model):
    """
    A day, or a run of days, this location is shut regardless of its hours.

    Its own table rather than deleting the hours for the week: a bank holiday is
    an exception, and a shop that expressed one by deleting its Tuesday would
    have to remember to put it back.
    """

    location = models.ForeignKey(Location, on_delete=models.CASCADE, related_name="closures")
    starts_on = models.DateField()
    ends_on = models.DateField()
    reason = models.CharField(max_length=200, blank=True, default="")

    class Meta:
        db_table = "knight_locations_closure"
        ordering = ("starts_on",)
        indexes = [
            models.Index(fields=["location", "starts_on"], name="knight_loc_closure_when"),
        ]
        constraints = [
            models.CheckConstraint(
                condition=models.Q(ends_on__gte=models.F("starts_on")),
                name="knight_loc_closure_ends_after_it_starts",
            ),
        ]

    def __str__(self) -> str:
        return f"{self.location_id} {self.starts_on}..{self.ends_on}"


class StaffMember(models.Model):
    """
    Somebody who works for this merchant.

    Not a user account, deliberately. A store's own accounts are the store's, and
    a chef who never signs in to anything still needs to appear on a rota; a
    foreign key to an account nobody creates would leave the rota empty for the
    common case — the same call the store's order history makes about its actor
    field.
    """

    code = models.CharField(max_length=40, unique=True)
    name = models.CharField(max_length=200)
    phone = models.CharField(max_length=40, blank=True, default="")
    email = models.EmailField(blank=True, default="")
    is_active = models.BooleanField(default=True)
    created_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        db_table = "knight_locations_staff"
        ordering = ("name",)

    def __str__(self) -> str:
        return f"{self.code} {self.name}"


class StaffAssignment(models.Model):
    """
    Somebody working somewhere, from a date and possibly until one.

    Dated rather than a plain link, because "who worked at the Camden branch"
    about last March is a question a merchant asks after an incident, and a
    membership row that was simply deleted when they moved answers it with
    nobody. A member of staff may hold several at once: cover across two branches
    is the normal case, not the exception.
    """

    staff = models.ForeignKey(StaffMember, on_delete=models.CASCADE, related_name="assignments")
    location = models.ForeignKey(Location, on_delete=models.CASCADE, related_name="assignments")
    role = models.CharField(max_length=80, blank=True, default="")
    starts_on = models.DateField()
    ends_on = models.DateField(null=True, blank=True)

    class Meta:
        db_table = "knight_locations_assignment"
        ordering = ("-starts_on",)
        indexes = [
            models.Index(fields=["location", "ends_on"], name="knight_loc_roster"),
        ]
        constraints = [
            # One open assignment per person per place. Two would put somebody on
            # the same rota twice, and the second one is always the mistake.
            models.UniqueConstraint(
                fields=["staff", "location"],
                condition=models.Q(ends_on__isnull=True),
                name="knight_loc_one_open_assignment",
            ),
        ]

    def __str__(self) -> str:
        return f"{self.staff_id} at {self.location_id}"


class MenuAvailability(models.Model):
    """
    A thing this location does not sell, or sells when others do not.

    An **exception table**, and that is the whole design. The alternative — a row
    per location per SKU saying "yes" — is a table that has to be written every
    time a product is created and that silently hides a new product from every
    branch when somebody forgets. Here, absence means available: a menu is what
    the store sells, minus what each branch has said it does not.

    `sku` is the store's own identifier held as a plain string, and `object_id`
    the row id beside it, the arrangement every Feature in this catalogue uses:
    a Feature may not reference a store's tables.
    """

    location = models.ForeignKey(Location, on_delete=models.CASCADE, related_name="menu")
    sku = models.CharField(max_length=100)
    object_id = models.BigIntegerField(null=True, blank=True, db_index=True)

    is_available = models.BooleanField(default=False)
    note = models.CharField(max_length=200, blank=True, default="")
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        db_table = "knight_locations_menu"
        ordering = ("location", "sku")
        constraints = [
            models.UniqueConstraint(
                fields=["location", "sku"],
                name="knight_loc_one_menu_entry_per_sku",
            ),
        ]

    def __str__(self) -> str:
        return f"{self.location_id} {self.sku} {'yes' if self.is_available else 'no'}"


class RuleKind(models.TextChoices):
    """
    How a rule decides.

    A closed list rather than an expression language. A merchant editing a
    routing rule at eleven at night is one typo away from sending every order to
    the wrong city, and every expression language eventually grows a debugger.
    """

    POSTAL_PREFIX = "postal-prefix", "The delivery postcode starts with this"
    CITY = "city", "The delivery city is this"
    ZONE = "zone", "The delivery zone is named this"
    ALWAYS = "always", "Anything not matched above"


class RoutingRule(models.Model):
    """
    Which location handles an order, and why.

    Ordered by an explicit priority rather than by row id, so that inserting a
    rule for one postcode above a rule for a whole city does not depend on the
    order somebody happened to type them in.
    """

    kind = models.CharField(max_length=20, choices=RuleKind)

    #: What the rule matches on. Empty for `always`, which matches everything and
    #: is how a merchant writes "and the rest go here".
    pattern = models.CharField(max_length=120, blank=True, default="")

    location = models.ForeignKey(Location, on_delete=models.CASCADE, related_name="rules")

    #: Lower is considered first. Named `priority` rather than `order` because
    #: this Feature is full of orders that mean something else.
    priority = models.PositiveSmallIntegerField(default=100)

    is_active = models.BooleanField(default=True)
    created_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        db_table = "knight_locations_rule"
        ordering = ("priority", "id")
        indexes = [
            models.Index(fields=["is_active", "priority"], name="knight_loc_rule_order"),
        ]
        constraints = [
            models.UniqueConstraint(
                fields=["kind", "pattern"],
                name="knight_loc_one_rule_per_pattern",
            ),
        ]

    def __str__(self) -> str:
        return f"{self.kind}:{self.pattern or '*'} -> {self.location_id}"


class OrderRouting(models.Model):
    """
    Where one order was sent, decided once and written down.

    `source_order_number` is the store's own number as a plain integer and it is
    unique here: an order is routed once. Re-deciding it on read would move last
    Tuesday's order to a different branch the moment somebody edited a rule, and
    the branch that actually cooked it would vanish from every report that
    matters.

    `rule` is kept for tracing and may become null when a rule is deleted, which
    is why `reason` is a copied string rather than a lookup. The explanation of a
    decision has to survive the deletion of the thing that made it — the same
    argument the store's own `OrderPromotion` makes about an uninstalled
    promotions Feature.
    """

    source_order_number = models.BigIntegerField(unique=True)
    location = models.ForeignKey(Location, on_delete=models.PROTECT, related_name="orders")
    rule = models.ForeignKey(
        RoutingRule,
        on_delete=models.SET_NULL,
        null=True,
        blank=True,
        related_name="decisions",
    )
    reason = models.CharField(max_length=200, blank=True, default="")
    decided_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        db_table = "knight_locations_order_routing"
        ordering = ("-decided_at",)
        indexes = [
            models.Index(fields=["location", "decided_at"], name="knight_loc_routed_orders"),
        ]

    def __str__(self) -> str:
        return f"order {self.source_order_number} -> {self.location_id}"
