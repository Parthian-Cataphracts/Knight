# KNIGHT, explained simply

Imagine a company that builds online shops for lots of different businesses — a
bakery, a clothing store, a restaurant. Instead of building each shop by hand and
then running around to fix them one by one, they built **one control room** that
looks after all the shops at once. **KNIGHT is that control room.**

## The big idea

Each shop is its own little building with its own front door (its own website and
address). KNIGHT doesn't live inside any shop — it's the control room across the
street that can:

- **open a new shop** when someone signs up and pays,
- **hand shops new abilities** (like "now you can give discount codes" or "now you
  can show product suggestions"), and
- **keep an eye on every shop** to make sure the lights are on and nothing's broken.

## The characters in the story

- **The shop owner (merchant):** someone who wants an online store. They sign up,
  pick a plan, pay, and — poof — get a working shop.
- **A shop:** the actual store customers visit. It stands on its own.
- **An ability (we call it a "Feature"):** a superpower you can switch on for a
  shop, like gift cards or delivery zones. The clever bit: KNIGHT builds each
  ability **once** and can install it into any shop that's paid for it — like
  writing a recipe once and cooking it in any kitchen, instead of reinventing the
  dish every time.
- **The control room (KNIGHT):** keeps the master list of who owns which shop, who
  paid for what, and which abilities each shop should have.

## A few helpful pictures

- The **database** is a giant, perfectly-organized library. Every fact — who signed
  up, who paid, which shop has which ability — is a book on a labeled shelf, so
  nothing gets lost and nobody grabs the wrong book.
- The **API** is the librarian. You don't wander the shelves yourself; you ask the
  librarian ("give me this shop's status") and they fetch exactly the right book.
- **Provisioning** is the moving-in crew. The moment a payment clears, the crew
  sets up the new shop — keys, address, furniture — with nobody having to phone
  anyone.

## What happens when someone signs up (the whole story in six steps)

1. A shop owner fills in their name and email.
2. They confirm their email (a link proves it's really them).
3. They pick a plan and pay.
4. The payment tells the control room: "they're good — set them up."
5. The moving-in crew builds the shop and switches on the abilities they paid for.
6. The owner opens their panel and sees their shop marked **Ready**.

No human in the control room had to lift a finger in the middle. That's the whole
point: shops open themselves.

## Why it's built this way

One tidy control room is easy to run and keeps every fact consistent (you never
want "paid" and "not set up" to disagree). But each shop stands on its own two feet,
so a busy bakery can't slow down the clothing store next door. Best of both worlds:
one brain, many independent bodies.
