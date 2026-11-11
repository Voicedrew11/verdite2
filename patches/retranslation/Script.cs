// The Retranslation: a new English translation made from the Japanese release, by
// PerdueEgg's verdite2-mods (mods/retranslation, Text.cs there). Picture messages,
// menu-font strings, code-built labels and the Encyclopedia's names. The mod's Movies
// table is left out: the port keeps the Japanese movie cards in a Japanese run.
using System.Collections.Generic;

namespace Kf2.Retranslation;

static class Script
{
    // These tables are the built-in translation. With a workbook in the data directory's
    // retranslation/ folder, Workbook.cs fills them from its Mod EN column instead (the
    // Text page's Import button, or at start), under this lock. verdite2-mods'
    // tools/build_text_cs.py writes the same tables (as class Text in the mod's
    // namespace; rename both to take a new copy).
    public static readonly object Sync = new();

    /// <summary>Picture messages: "T" + TALK.T entry or "I" + ITEM.T entry. A line break
    /// starts a new line; the rest is wrapped to the picture by Kf2.Text.Pictures.</summary>
    public static readonly Dictionary<string, string> Messages = new()
    {
        ["T0"] = "Haven't seen your face before. Came for the crystals like the rest, no doubt. If you've no wish to die, you'd best buy your gear here.",
        ["T1"] = "All my gear was made in Granatyki. Human work, mind you, but fine work all the same.",
        ["T2"] = "Every monster has its own nature, so not just any weapon will serve. Best buy a few kinds.",
        ["T3"] = "Crystals are hard to come by these days.",
        ["T4"] = "Seath is the god the High Elves worshipped. Not that she ever did us Dark Elves any good.",
        ["T5"] = "Shoddy gear won't keep you alive here.",
        ["T6"] = "My grandson Raffy is hopeless. He's got my blood in him, so he ought to have a head for trade, but I'd not trust him with anything of value.",
        ["T7"] = "A fair few Verdian soldiers came through, so I sold them some gear. Not that it'll do them much good.",
        ["T8"] = "Bring me something good, and I'll pay well for it.",
        ["T9"] = "There's a fellow called Fai Fat down below, near my grandson. He has something I want, but he won't sell. Stole it from the Hierophant, yet he has the gall to make outrageous demands for a trade.",
        ["T10"] = "You'd do well to buy more gear.",
        ["T11"] = "You'd do well to buy more gear.",
        ["T12"] = "You'd do well to buy more gear.",
        ["T13"] = "Plain crystals won't turn a profit. If you want a good price, bring me colored crystals, or other gems.",
        ["T14"] = "Time was, plain crystals alone turned a fine profit.",
        ["T15"] = "The Mirror of Truth is said to be found only in Verdite, yet it seems one is hidden on this island too. Could you get your hands on it for me?",
        ["T16"] = "High Elves are skillful and clever, I'll grant them that. But I can't stand how stuck-up they are.",
        ["T17"] = "Now, now. You mean to go about in that gear?",
        ["T18"] = "They say there's a shrine somewhere below here. 400 years I've lived, and never once seen it. Left by the High Elves, they say, who perished long, long ago.",
        ["T19"] = "That old codger in the graveyard is definitely hiding something good.",
        ["T20"] = "I don't pick and choose who I deal with. There are good customers and bad in every age, and the Hierophant is one of the better ones.",
        ["T21"] = "The King of Verdite is too stiff to deal with. Played right, he could make a fortune.",
        ["T22"] = "It might be time to wrap up my business here.",
        ["T30"] = "Well done, making it this far. Some fellow named Arnes has come too, heading farther in. Looking for someone, it seems, but there's no one about but the Hierophant's soldiers.",
        ["T31"] = "Came all the way down here, and it's hardly any different.",
        ["T32"] = "Long ago there was a High Elf warrior named Merel Ul, and I'm selling the very boots he wore. You'd be a fool to pass them up.",
        ["T33"] = "That Kiel was never much use.",
        ["T34"] = "Everything I deal in will serve you well from here on.",
        ["T35"] = "Fire gear would make short work of that ice monster, but such gear is hard to come by.",
        ["T36"] = "Riches lie sleeping in that shrine, I'm sure of it. But it's sealed now, so there's no getting in.",
        ["T37"] = "The Big Mine is full of crystals worth good money. If you want better gear, go and fetch some. Don't hold your life too dear.",
        ["T38"] = "Seems the crystals really are dwindling these days.",
        ["T39"] = "The Poison Cave is full of crystals. I told Theo as much the other day.",
        ["T40"] = "They say the Hierophant is somewhere below here, though I've never met him myself. He keeps to a great hall known as the Coliseum, it seems.",
        ["T41"] = "You know that elf, Leon? I offered him good money, but he won't make a thing for me. I told the soldiers about him, and it seems they've taken him.",
        ["T42"] = "Been to the castle ruins yet? King Harvine's treasure is in there. Oh, I know everything, I do.",
        ["T43"] = "Raffy? He's nothing but a boatman. I only bring him along because I need him to reach the island. I'd never trust him with business.",
        ["T44"] = "Useless, every last one of them...",
        ["T45"] = "Bring me the Cyclops Collar and I'll pay a high price. It was worn by a one-eyed giant, made by a mage in ancient times. Quite something. Hm? I told you, I know everything.",
        ["T46"] = "Meryl? I know far more than she does. Besides, she does nothing but get in the way of my business.",
        ["T47"] = "The fiend Galth Fee is the pride of us Dark Elves. His gear was a gift from the Slumbering One, you know.",
        ["T48"] = "Business on this island gets harder by the day.",
        ["T60"] = "Welcome! I've nothing very special, I'm afraid, but if you mean to travel this island, you'd do well to buy something.",
        ["T61"] = "Every piece of gear has its own traits, so it's best to choose carefully.",
        ["T62"] = "Have you anything that would astonish old Celffy? I'd pay a high price for something like that.",
        ["T63"] = "A weapon that's too heavy won't do you any good in the end. Choose one that suits your strength.",
        ["T64"] = "I'm fine, but I don't think you'll manage the passage. There are soldiers at the guard post along it, and past that is the jail, where anyone suspicious is taken.",
        ["T65"] = "I wish old Celffy would acknowledge me. He sees me as nothing but a boatman.",
        ["T66"] = "Just rowing the boat over here is hard work.",
        ["T67"] = "Welcome. Please make sure you're well equipped.",
        ["T68"] = "Old Celffy often tells me tales of Galth Fee, the dreaded fiend. I suppose he means for me to go find something valuable like that.",
        ["T69"] = "They say Seath, the god of the High Elves, never granted us Dark Elves a single blessing.",
        ["T70"] = "Welcome. Please make sure you're well equipped.",
        ["T71"] = "Have you heard of a sword called the Spider? If not, don't worry about it.",
        ["T72"] = "Rumor has it the present King of Verdite is a fine swordsman. I wonder what sort of sword he uses.",
        ["T73"] = "Only old Celffy ever goes down below. I'd love for him to take me just once. He says there are monsters there that breathe fire. I'd like to see them for myself.",
        ["T74"] = "I'd like to make money, but lately I think a modest amount is enough.",
        ["T75"] = "Welcome. Please, don't push yourself too hard.",
        ["T90"] = "You look like one from the ship that went down the other day. Rare for anyone to wash up alive. Every ship but the merchants' gets sunk.",
        ["T91"] = "I came here after the pirates' treasure, but this island is rife with poison. Now I can't live without the island's water.",
        ["T92"] = "See that lighthouse? Harvine III had it built. They say it still burns by the magic of Tzedek, the mage who served at his side. You can reach it through the shallows, but mind the Seagills. Vicious fish.",
        ["T93"] = "It's dangerous around here. Go a little farther in and there are terrible monsters. Above all, stay well clear of the giant Kraken's nest nearby.",
        ["T94"] = "Up on the higher ground, a few people still live. Merchants come and go too. Go and have a look.",
        ["T95"] = "The village is past the wooden door. Wander about here dressed that lightly and a dozen lives won't be enough. Get going.",
        ["T96"] = "Take my advice. Go, and the sooner the better.",
        ["T97"] = "The seaside fountain will ease a weary body. The elves made it, I hear, drawing water from the Fountain of Healing that Seath gave them.",
        ["T98"] = "Doors are sometimes hidden in the walls, but don't go searching carelessly. No telling what might jump out.",
        ["T99"] = "My grandfather told me the pirates of old used this island as a base for their raids. In the end they turned on each other, and many died. Over the hidden treasure, I hear.",
        ["T100"] = "There are a few caves along this beach. They're dangerous. Watch yourself.",
        ["T101"] = "On the far side, the east of the island, there's another beach. But you'd have to go all the way through the passages the monk-soldiers guard. I doubt you'll manage it.",
        ["T102"] = "The other day, a little boy called Sandler was all excited because he'd found a key on this beach. Said it was the pirates' key. I doubt it's anything special.",
        ["T103"] = "I just want to fish in peace.",
        ["T104"] = "You killed the Kraken? Not bad. This beach will be a little quieter now.",
        ["T105"] = "Still, I wonder how long this night will go on.",
        ["T110"] = "My name is Nora. It has been two years since my brother Dias crossed to this land, and I have heard nothing from him. If ever you should meet him, please... tell him of me.",
        ["T111"] = "My brother came to this island because he heard a great warrior was here. Whether he meant to cross swords with him or to learn from him, I cannot say.",
        ["T112"] = "Most of this island lies in darkness. Why is this place alone granted daylight? Being here fills me with guilt, and with the wish that the whole island might be like this.",
        ["T113"] = "Fai, who crossed to this island with my brother, is missing too. Back home he often came to visit, and he was so very kind. Why have neither of them sent me even a word?",
        ["T114"] = "Where can my brother be...",
        ["T115"] = "I only recently noticed, but the people who lived nearby are vanishing, one after another. It frightens me so, and I am so alone. Will I never leave this place?",
        ["T116"] = "Visitors rarely come here. Not long ago a Verdian nobleman named Arnes came by, but it seems the Hierophant's soldiers seized him and took him away.",
        ["T117"] = "Close by, there is a door that never opens. The relief carved upon it is said to be Seath, whom the High Elves worshipped.",
        ["T118"] = "On the pedestal of the guidepost nearby, there is a key. I don't know who left it, but it isn't mine to take, so I left it be.",
        ["T119"] = "Al came by and told me that a wounded Verdian soldier had escaped from the jail, and said there was a hidden door in it.",
        ["T120"] = "I wish we could go back to those happy days.",
        ["T121"] = "From what Al says, the Hierophant is a fearsome man. If my brother met him, they will surely have crossed swords. I pray my brother is safe.",
        ["T122"] = "I hope I can see my brother again soon.",
        ["T123"] = "Sometimes someone leaves food for me. I once caught a glimpse of him from behind, and I think it may have been Fai.",
        ["T124"] = "Why won't Fai come to see me? I will go on praying for my brother's safety, always, trusting that my prayers will reach the god of the earth...",
        ["T130"] = "Aleph, you have come at last. The time has come to go forth by your own strength and learn the truth. I shall teach you the meaning of what was made upon this island by Seath, god of the High Elves, and by Ghirah, the Slumbering Darkness.",
        ["T131"] = "Trust in what this crystal ball tells you, and choose your path by it.",
        ["T132"] = "Besides the works of Seath and Ghirah, there are many things wrought by the magic of mages. Harvine III's Flute of Wind, for one, and the Flame Sword of Tzedek, his mage.",
        ["T133"] = "Even this crystal ball cannot see where your road will end.",
        ["T134"] = "To restore the Fountain of Healing, you will need the Dragon Stones that Seath made. The crystal ball shows all three stones gathered, and water springing forth.",
        ["T135"] = "The crystal ball is trying to tell us something of its own accord.",
        ["T136"] = "Harvine III, called the King of Wind for his magic, was a king of great power. Yet he lacked foresight, it seems. Had he heeded my warning and not built his castle, he would never have fallen.",
        ["T137"] = "All that the crystal ball reveals is truth.",
        ["T138"] = "The crystal ball says you did not come here by chance. It was Seath's hand that brought you.",
        ["T139"] = "Seek out Harvine III's Flute of Wind. Without it, your way forward will not be easy.",
        ["T140"] = "Tzedek, the Mage of Fire, left his magic there to keep the lighthouse burning. In time, the Fire Crystal was born of that magic.",
        ["T141"] = "The owner of the Flute of Wind hides deep within a cave. You would do well to meet him.",
        ["T142"] = "The Dragonlord Trees were made by the earth. And this crystal ball, too, was a gift of the earth.",
        ["T143"] = "The ice warrior Kel Fargus was transformed by Ghirah, and is now a monster of ice. He wielded a sword called the Ice Blade, and was a swordsman of great skill, it is said.",
        ["T144"] = "To defeat Tarn, the ice monster, you will need strong fire magic, and fire equipment.",
        ["T145"] = "Ghirah now sleeps, drawing back his true power. And so warriors called the Guardians stand watch over the path that leads to him.",
        ["T146"] = "The Guardians are the warriors hailed as the mightiest of their age. Among them were the ice warrior Kel Fargus, and Galth Fee, the fiend who slew the High Elf hero Merel Ul.",
        ["T147"] = "Trust in what this crystal ball tells you, and choose your path by it.",
        ["T148"] = "Many warriors have guarded the Slumbering One. Of them, the only mage was Schudom Lint, who kept his watch with a one-eyed giant made by his magic.",
        ["T149"] = "All that the crystal ball reveals is truth.",
        ["T150"] = "Besides his own labyrinth, the mage Tzedek built a chamber to guard the king's treasure. You might visit that place, Harvine III's treasure house, as it were.",
        ["T151"] = "All that the crystal ball reveals is truth.",
        ["T152"] = "The High Elf warrior Merel Ul was a mighty warrior called the Dragon of the Forest. Yet he could never have defeated Ghirah. Seath must have known as much.",
        ["T153"] = "All that the crystal ball reveals is truth.",
        ["T154"] = "No one has laid Galth Fee's gear to rest. It lies scattered about his grave still.",
        ["T155"] = "The crystal ball says the future rests on you alone. Yet how it will end, even the crystal ball cannot see.",
        ["T160"] = "My dad hasn't come home in three days. He left saying this time he'd find a big crystal for sure.",
        ["T161"] = "I want to go look for him, but down below is way too scary. I just can't.",
        ["T162"] = "And Leon's gone too. What am I supposed to do?",
        ["T163"] = "Dad's safe, isn't he? He has to be.",
        ["T180"] = "Dad says he's not going crystal hunting anymore. I'm so glad. Now I don't have to worry.",
        ["T181"] = "Sandler hasn't come over to play lately. I wonder what he's up to. I hope he's not doing anything dangerous.",
        ["T182"] = "Leon's been gone so long now. Sometimes I go to his house because I worry about his flowers, but nobody's there, and it's so lonely.",
        ["T183"] = "To get to Leon's house you have to go through a place that's nothing but graves, so it's really scary. There's a kind old man there, but there are lots of monsters too.",
        ["T184"] = "I don't want to stay on this island much longer.",
        ["T185"] = "Between here and, um, the 3rd Guard Post, there's a door that wouldn't open before. Now it's open, and there's a fountain inside. The water's good for you, I think, because lately I've been feeling so much better.",
        ["T186"] = "Dad told me you're the one who's going to save everyone.",
        ["T187"] = "I can't wait to see an even bigger field of flowers.",
        ["T200"] = "I kept looking for more and more crystals, and came all the way in here. I remember a monster attacking me, but after that... Thank you for saving me.",
        ["T201"] = "Gigi will be worried, so I must hurry back to the village. Please stop by my house later. Promise me.",
        ["T202"] = "I can't wait to see Gigi's face.",
        ["T220"] = "I have no more need of this. Please, take it.",
        ["T221"] = "Please, take great care.",
        ["T240"] = "I've gathered plenty of crystals, so now Gigi and I will live quietly, just the two of us. I hope I can take her home to our own country one day.",
        ["T241"] = "Gigi has been lonely since Leon disappeared. I hear he was taken farther down, past the 1st Guard Post.",
        ["T242"] = "When I lost my life in the Big Mine, I don't recall where I wandered, but I found a stone floating in the air. I couldn't take it. Could that have been the black stone that Seath is said to have given?",
        ["T243"] = "Leon told me that two keys are needed to enter the shrine, but they were cast into the sea long ago. It's a tale handed down among the elves, he said, but it's so old no one can be sure.",
        ["T244"] = "Leon's mother was so kind, yet the poison took her. I've had my fill of this island.",
        ["T245"] = "Sandler came by the other day. He said he gave the pirates' key to someone who saved him from the termites.",
        ["T246"] = "I overheard some monk-soldiers. There's some sort of laboratory near the Coliseum, where they make monsters. If the very monsters tormenting us are made there... nothing could be more terrible.",
        ["T247"] = "I wish Gigi and I could go home soon.",
        ["T260"] = "You have come far. It has been long since my son Leon was taken by the Hierophant. Here is a Figure of Seath that Leon made. Take it, and I beg you, rescue my son.",
        ["T261"] = "The wish we elves could not fulfill... I beg you, fulfill it with your own hands.",
        ["T280"] = "You met my mother...? I see... If I can be of any help to you, I will spare no effort. Please, visit my house.",
        ["T281"] = "Thank you for saving me. If I can be of any help to you, I will spare no effort. Please, visit my house.",
        ["T282"] = "May holy Seath protect you. I shall pray for you with all my heart.",
        ["T283"] = "Seath will surely lend you her strength.",
        ["T300"] = "In truth, my mother passed away three years ago, of the poison that had built up in her body. To the very end, she worried for the people of the island. Your coming must have kindled a light of hope in her.",
        ["T301"] = "I cannot make two Figures of Seath at once. I will make you a new one each time you need it, so please return when the time comes.",
        ["T302"] = "I'm worried for Gigi. Theo went into the Big Mine and has not been heard from since. Please, rescue him with the power of the Figure of Seath.",
        ["T303"] = "Gigi must be waiting for Theo.",
        ["T304"] = "It is said that long ago, Seath bestowed a crystal upon us. It gives off a black light, and holds immense power. A sword forged from that crystal would have might beyond imagining.",
        ["T305"] = "If you find the black crystal, please bring it to me.",
        ["T306"] = "I will make the finest sword there ever was, I promise you. If it is my mother's wish, and the wish of all we elves, I would not begrudge my life. But I must have time. Please, leave me be awhile.",
        ["T307"] = "Please, wait a little longer.",
        ["T308"] = "Forgive me. What I feared has come to pass. The Hierophant found the finished sword, and took it. But he should not be able to wield it.",
        ["T309"] = "Forgive me for keeping you waiting. The finished sword lies upon the table. Please, grant our wish.",
        ["T310"] = "The Dark Slayer holds the power to awaken the lost equipment of the High Elves. When Merel Ul was defeated, three of his six lost pieces were gathered and sealed away by Seath.",
        ["T311"] = "With the Dark Slayer, you will surely defeat Ghirah.",
        ["T320"] = "New face, eh? If it's crystals you're after, sounds like they're underground. Go have a look. Me, I don't go down there. Too fat. Anyway, I've got herbs and such, if you're buying.",
        ["T321"] = "Here's a tip. Green slimes cure poison. If poison's ever got you in a real bind, try stepping on one.",
        ["T322"] = "See a monster, you run. Don't go getting ideas just because you can swing a sword.",
        ["T323"] = "Lately I'm hungry all the time, for some reason.",
        ["T324"] = "There's an elf merchant set up shop around here, but his prices are steep. My herbs are cheaper, but they hardly sell. Maybe it's that water that's supposed to heal you when you drink it.",
        ["T325"] = "The monk-soldiers work the bridge switch, to stop folks moving about as they please.",
        ["T326"] = "Don't go near the door at the 1st Guard Post that leads underground. Step inside and you're dead on the spot, no questions asked.",
        ["T327"] = "Believe it or not, I used to be real thin.",
        ["T328"] = "Ever heard of the Fountain of Healing? Made by Seath, the High Elves' god, so the story goes.",
        ["T329"] = "Folks around here wouldn't do what the Hierophant told them, so he cut off our water and food. But my brother Kiel brings us food, and there's water at Nora's place, so we're getting by.",
        ["T330"] = "We're not going hungry, for now.",
        ["T331"] = "I don't go far, on account of the monsters, but there's a fair bit to eat underground.",
        ["T332"] = "We used to come and go between the villages pretty freely, but now even the passages are locked and heavily guarded. I hope nothing terrible's about to happen.",
        ["T333"] = "I heard Verdian soldiers have slipped onto the island. Maybe that's why all the guards. Does King Alfred want this island too, I wonder?",
        ["T334"] = "Every now and then someone comes and goes from Nora's place. Doesn't seem to be stealing anything, though. Who could he be?",
        ["T335"] = "Carry as many herbs as you can.",
        ["T336"] = "An elf merchant comes and goes at Kiel's place. For whatever reason, they seem to get along fine.",
        ["T337"] = "Krola's boy Sandler is a real prankster. A handful, that one. Haven't seen him in a while, though. Wonder what he's up to.",
        ["T338"] = "There's a Big Mine and a Small Mine on this island, and the Small Mine's across the bridge. Not that you'll find much there.",
        ["T339"] = "They won't let the likes of me across that bridge. Jose's house is that way, and the elf's grandson should be over there doing business.",
        ["T340"] = "What I wouldn't give for something tasty.",
        ["T341"] = "Kiel hasn't brought us food in a while. Only Kiel can get past the guard post. What do I do now?",
        ["T342"] = "I want to help Nora, at least. Somehow.",
        ["T350"] = "Come for the crystals too, have you? This leg of mine was crushed in a rockfall. The friend I lost that day lies here, and I keep watch over his grave. I mined crystals myself once, but never found aught of value.",
        ["T351"] = "Everyone worked hard, but the crystals we found barely kept us alive. The merchants take advantage, and yet the poison won't let us go home.",
        ["T352"] = "Since my leg went bad, I've not set foot in the mines. I scrape by on the crystals my friends left behind. But ever since the Hierophant came, life's grown harder still.",
        ["T353"] = "Farther in lie the graves of Harvine III's soldiers, the ones left behind when the king withdrew. They believed he would return. I can almost hear their bitter cries even now.",
        ["T354"] = "Of late, I often dream of my younger days.",
        ["T355"] = "The lighthouse the mage lit still shines over the sea. Harvine III's soldiers watched that light, waiting for ships that never came, until they died.",
        ["T356"] = "There ought to be a map of the mines around here somewhere. Find it, and take it with you. The young miners never listen. Lend an old man your ear now and then.",
        ["T357"] = "Did you find the map? I'm sure I left it hereabouts. Age is a cruel thing.",
        ["T358"] = "There are countless graves here. They say the High Elves made graves here once, but in all my years here, I've never seen a High Elf grave.",
        ["T359"] = "There are plenty of monsters about, yet not once have they set upon me.",
        ["T360"] = "You're no mere treasure hunter, are you? I'll not ask why... Here. Take this bell.",
        ["T361"] = "Gigi is a good girl. Please, look after her.",
        ["T362"] = "It seems it's about time I went to join my friends.",
        ["T370"] = "I've not seen your face around here. Another soldier sent to look into the Hierophant, I suppose. He is a man of terrible power. I was with him for many years. Now, as you see, I hide.",
        ["T371"] = "The Hierophant's soldiers fight until they die. As if they had no will at all. That is what frightens me.",
        ["T372"] = "Staying on this island any longer is far too dangerous. There is something I need in order to leave. Bring it to me, and I'll trade you something good for it.",
        ["T373"] = "Bring me the Figure of Seath the elves speak of, and I'll trade you the Moon Gate for it.",
        ["T374"] = "This Moon Gate is vital for getting around the island. The Hierophant had it, but it seems Seath first gave it to the elves.",
        ["T375"] = "I'll trade you this flute for three Crystal Flasks.",
        ["T376"] = "Truth is, I don't know what it's for. But it was the Hierophant's, so it must be important.",
        ["T377"] = "I don't care what becomes of me, but I fear for Nora. With enough crystals, sending her home is not beyond hope.",
        ["T378"] = "The three of us used to play together. Nora... and that Hierophant, Dias...",
        ["T379"] = "Dias can never be what he once was. The kindest thing would be to defeat him as a warrior, but I could never manage it. I must do something before Nora hears of this.",
        ["T380"] = "It all feels so hollow.",
        ["T381"] = "Dias believes in Ghirah. But that black dragon, a god? I cannot believe it.",
        ["T382"] = "Nora used to say the god of the earth made the Dragonlord Trees. That must be true. But Seath, the High Elves' god... I have my doubts about her too.",
        ["T383"] = "I'll trade you this map for three Crystal Flasks.",
        ["T384"] = "This map will help you defeat the Hierophant.",
        ["T385"] = "How did it ever come to this?",
        ["T386"] = "How did it ever come to this?",
        ["T387"] = "How did it ever come to this?",
        ["T388"] = "I need nothing more. Take this Light Crystal. You alone can defeat Dias. Please, save the people of this island. But what am I to tell Nora...",
        ["T389"] = "How did it ever come to this?",
        ["T400"] = "Who're you? Why're you dressed like that? Oh, if it's crystals you want, there ain't many left around here.",
        ["T401"] = "I didn't come to dig crystals neither. I came after the treasure Harvine III left behind. Oops. Keep that under your hat.",
        ["T402"] = "My ancestor was one of Harvine III's close retainers, and he was trusted with something called the King's Key. Pretty impressive, huh? Thing is, I got a little carried away looking for where to use it, and, uh, lost it.",
        ["T403"] = "Where'd I drop it? I should be laughing all the way to the bank by now.",
        ["T404"] = "They say even Harvine III gave up building his castle on this island halfway through. The monsters here must be something else. The poor soldiers left to guard the treasure got wiped out to the last man.",
        ["T405"] = "I've been all over this island till I'm sick of the place, but for some reason I still can't find the castle ruins. What if I stepped on 'em and squashed 'em by accident? Nah, couldn't be that small.",
        ["T406"] = "Er, what was I saying? Oh, right. After he gave up on the castle, what was waiting for Harvine III was ruin. The three kingdoms we've got now were split from the land he ruled.",
        ["T407"] = "Harvine III had magic too, and he was so good with wind magic they called him the King of Wind. They say things with his magic sealed inside are still around.",
        ["T408"] = "Where'd that key get to... Er, what key was it again?",
        ["T409"] = "I was looking for something, I'm sure...",
        ["T420"] = "This is an old mine tunnel. Hardly any crystals left. The miners from the middle of the island go deep underground to the Big Mine, but it's like walking to their deaths.",
        ["T421"] = "Don't know what you're after, but if you're headed to the Big Mine, hear old Harris out first. He lives quiet in the graveyard of his old mates, in the east of the island.",
        ["T422"] = "The monk-soldiers have guard posts along the passages. Not all of them are known, but they say there are six. I know the one called the 5th Guard Post, just before the Big Mine.",
        ["T423"] = "You saw all those slimes around here? Swords are near useless on them. Only something pointed works, or fire.",
        ["T424"] = "Somewhere underground is a cave the elves made, where they hid for a time before they were wiped out. What the Hierophant's after is in there, it seems, and the miners from the middle of the island, the ones he gave daylight, are all hunting for it.",
        ["T425"] = "The things around here are called termites. They breed in a nest near the central village. There are crystals there too, but you won't catch me going in after them.",
        ["T426"] = "Termites are weak to arrows. If you trust your aim, give it a try.",
        ["T427"] = "Vile things, those termites.",
        ["T428"] = "I want that Whispering Bell old Harris told me about too. I've dug and dug, and not so much as a shard of crystal.",
        ["T429"] = "My house is on the south side. But the folk near Al's house on the north side... they all died. Terrible thing.",
        ["T430"] = "A few men calling themselves Verdian soldiers came through a while back, but no one's come lately. Maybe the Hierophant killed them. Or his soldiers caught them first.",
        ["T431"] = "I never should've believed the stories about all those crystals and come to this island.",
        ["T432"] = "They say there's a fountain north of the Small Mine, but the water doesn't seem to reach this far.",
        ["T433"] = "If only the termites would go away, at least.",
        ["T440"] = "You're not from this island, are you? Looking for Theo? Come to think of it, he said he was going somewhere with lots of crystals. Haven't seen him since.",
        ["T441"] = "Sure, there are more crystals farther in. Doesn't mean I'm going. Monsters everywhere, and they say there's even a cave brimming with poison.",
        ["T442"] = "Seems what the Hierophant's after is in the Elf Cave, but we can't find the Elf Cave no matter what we do.",
        ["T443"] = "All this digging, and not a single shard. What's going on?",
        ["T444"] = "All they told us is it's a stone of an odd color. That's it. And we're not to come back till we find it. Unreasonable, if you ask me.",
        ["T445"] = "If I'd known, I'd have borrowed the old man's map. Figured it was too old to be any use.",
        ["T446"] = "Have to find it fast and get back...",
        ["T460"] = "Everyone calls this place the Big Mine. Farther in are the Poison Cave, the Cave of Darkness, the Cave of the Earth Spirit, and the Elf Cave. No one knows exactly where.",
        ["T461"] = "I hear an Earth Elemental dwells in the Cave of the Earth Spirit, guarding something. Long, long ago, a mage lived there too, so the legend goes.",
        ["T462"] = "Long ago the elves built a shrine on this island, and when they finally perished, there was a cave where they hid at the last. That cave is called the Elf Cave.",
        ["T463"] = "I'm tired of digging.",
        ["T464"] = "I heard Verdite has a new king. A good one, by all accounts, unlike the last.",
        ["T465"] = "There's a tale that a Verdian mage hid treasure in the Cave of Darkness. I hear plenty of tales like that, but there's an awful lot on this island wrapped in mystery.",
        ["T466"] = "Stay away from the Poison Cave. I often see folk go in after crystals. None come back alive.",
        ["T467"] = "The poison in the Poison Cave was spread to wipe out the elves, and it has filled the cave ever since.",
        ["T468"] = "Nothing matters if you're dead. Don't push your luck.",
        ["T480"] = "If you have crystals, I'll make you something good. It's sure to come in handy.",
        ["T481"] = "Crystal Flasks have a strange power: They can draw healing water. Stranger still, they can't hold ordinary water.",
        ["T482"] = "I'm the only one on this island who can make the Crystal Flasks you need to carry healing water.",
        ["T483"] = "I make arrows from the crystal left over from the flasks. You'll need them before long. Some monsters come at you through the air, you know.",
        ["T484"] = "A bow and arrows really do come in handy.",
        ["T485"] = "I lied to you, actually. I'm not the only one who can make Crystal Flasks. Leon, who taught me how, can too. But he says some sense of mission is driving him, and he wants to devote himself to it, so he isn't making them now.",
        ["T486"] = "Arrows work well on those termites. There are lots of them in the mine, so you'll definitely need some.",
        ["T487"] = "These days the magic of monsters and mages lies stagnant on this island, so all kinds of crystals turn up. You can even use them to learn magic.",
        ["T488"] = "Better buy as many arrows as you can.",
        ["T489"] = "A while back, someone dressed like a monk came and asked me for Crystal Flasks. Said he'd give me a wind something-or-other in exchange, but I turned him down. Who knows if a merchant would even buy a thing like that?",
        ["T490"] = "He was acting sneaky, so I figure he stole it. Besides, I can't trust someone I've never seen around here.",
        ["T491"] = "This island has two mines, the Small Mine and the Big Mine. The Small Mine was dug long ago, so there are hardly any crystals left in it.",
        ["T492"] = "The Big Mine was started fairly recently, but the digging broke into old caves, and now it's every bit as vast as its name suggests. The Hierophant's looking for something down there, so it's crawling with monk-soldiers.",
        ["T493"] = "Better buy as many arrows as you can.",
        ["T494"] = "There are fewer crystals to be found as it is, and with monsters that eat crystals, it's hopeless. Kill one and you might get some back, but don't count on it.",
        ["T495"] = "The Hierophant's soldiers with bows, the archers, mostly shoot from high places. Their arrows are ones I made, too.",
        ["T496"] = "There's a weapon called the Arbalest, and I'm told my arrows won't do for it. It takes Elf Bolts, made by elven craftsmen. I can't believe their skill is that much better than mine.",
        ["T497"] = "The crystals have dwindled, and so have the people who come to the island.",
        ["T498"] = "They say there's something called the Lightwave Ring. With that, my arrows would be plenty useful.",
        ["T499"] = "Looks like this business of mine is nearly done.",
        ["T510"] = "My son Sandler hasn't come home. He's only a little boy. Have you seen him? He's so wrapped up in treasure hunting that even when I scold him, he goes off to play deep inside. I hope nothing's happened to him.",
        ["T511"] = "He keeps bringing home junk and calling it treasure... This time he said he'd found an ants' nest, so I'm worried sick. The ants here are big and terribly fierce.",
        ["T512"] = "The only place I can think of is Meryl's. She came from the continent recently, I hear, and he seems to listen to all her stories. He must have found the ants' nest near there.",
        ["T513"] = "Always making me worry... What am I to do with that boy?",
        ["T530"] = "I hear you saved Sandler. I truly don't know how to thank you. Thank you so much. But please, don't believe a word Sandler tells you. He's only a child.",
        ["T531"] = "I worry Sandler will get into mischief again. If only Gigi were here... With the bridge to the East Village as it is, there's nothing I can do.",
        ["T532"] = "He's been behaving lately, but I can't take my eyes off him, or he'll disappear again.",
        ["T533"] = "I don't know if it will help, but a crystal miner, now passed on, once told me of a bell that can find crystals.",
        ["T534"] = "Gigi's father sometimes goes deep into the Big Mine looking for crystals, I hear. They say it's a very dangerous place.",
        ["T535"] = "Everyone around here works for the Hierophant and goes to the Big Mine. Thanks to that, we needn't worry about food, but...",
        ["T536"] = "I hope days of peace come soon.",
        ["T537"] = "Sandler went to Gigi's house and came back. He says Theo still hasn't returned. They told him it was dangerous, but he went deep into the mine anyway... Gigi said she didn't want to leave her house, so Sandler gave up and came home.",
        ["T538"] = "There's an earth spirit in the Big Mine called the Earth Elemental, and it has already killed many crystal miners. Please, don't go to a place like that.",
        ["T539"] = "Sandler's stopped his mischief lately, so I'm a little relieved. I hope he stays good like this.",
        ["T540"] = "I hope everyone comes back from the mine soon.",
        ["T550"] = "Thanks! Those things were trying to eat me! That big one was in the way and I couldn't go anywhere. Here, have this key, to say thanks. I found it down by the sea. I bet you'll find some amazing treasure!",
        ["T551"] = "My dream is to go on adventures all over the world. Mom'll worry, but it can't be helped, right?",
        ["T552"] = "Mom must be really worried about me.",
        ["T570"] = "Mom was really mad. She says I can't go outside anymore. I was going to go tell Gigi all about what happened...",
        ["T571"] = "Hey, did you use that key? I hardly ever get to go all the way to the sea... Find the treasure for sure, alright?",
        ["T572"] = "Did you see the bridge on the side of the cliff at the West Seaside? I was too scared to jump down, but I bet there's something there.",
        ["T573"] = "I heard Uncle Leon's mom is a High Elf. What's a High Elf? Are they important?",
        ["T574"] = "Gigi says she got her pendant from Uncle Leon. Her dad wears the same one. I want one like that too.",
        ["T575"] = "Past the 2nd Guard Post near here, on the way to Gigi's house, there are pillars that look like crystals. I think they've got something to do with King Harvine. I've seen that crest before, so I'm sure of it!",
        ["T576"] = "I wonder how Gigi's doing. I wanna see her.",
        ["T577"] = "If I could go to the mine, I bet I'd find tons of crystals.",
        ["T578"] = "There's a lighthouse on the east sea too. It's my special secret. Nobody else knows!",
        ["T579"] = "Someday I'll find the treasure for sure and go home with Mom. Mom's always telling me about the forests of Verdite.",
        ["T580"] = "A while back some Verdian soldiers came. I bet they came to beat the Hierophant. Mom says he's a bad guy.",
        ["T581"] = "Someday I'm gonna go on an adventure too.",
        ["T590"] = "Treasure hunting? I hear that on this island especially, many kings, high officials and mages hid treasures in secret, and there they still lie. The chests and doors are locked, but I can make you any key you need.",
        ["T591"] = "Nobody on this island can make keys but me.",
        ["T592"] = "Truth be told, I don't want to be in a place like this. A thief called Gil tricked me into copying a key, and I've been on the run ever since.",
        ["T593"] = "Please don't put my keys to bad use.",
        ["T594"] = "Bestowed by Seath or by anyone else, if it's called a key, there's none I can't make.",
        ["T595"] = "The Hierophant's soldiers keep constant watch on the main underground passages. Even if you beat them, the doors are locked. To get through, you need the Monk-Soldier's Key.",
        ["T596"] = "From the place the monk-soldiers guard, called the 1st Guard Post, a stairway leads down. Never go there. Not that you could even reach the 1st Guard Post, I expect.",
        ["T597"] = "The monk-soldiers' doors are locked from both sides, and open only from the side where the key is kept.",
        ["T598"] = "Customers have stopped coming lately, for some reason.",
        ["T599"] = "Not long ago someone asked me to make a key to the Verdian treasury. It came out well, so I'm sure it proved useful.",
        ["T600"] = "Stay on this island too long, and the poison will keep you from ever leaving.",
        ["T601"] = "I'd like to try making an unusual key once in a while",
        ["T602"] = "I'd like to try making an unusual key once in a while",
        ["T603"] = "It seems there's a castle of Harvine III's on this island. Oh, no, it's just that Dalf was saying so. He showed me something he said was the King's Key. I should have made a copy of it then.",
        ["T604"] = "Have you heard of something called the Skull Key? Well, that's a secret.",
        ["T605"] = "I wonder what Gil is up to now. Stealing again, I suppose.",
        ["T606"] = "This is just a rumor, but they say there's an elven shrine underground. They must have worshipped something there. I hear there were two keys to it, but they were thrown into the sea. If I could find even one, I could make a copy.",
        ["T607"] = "I've built up quite a stock of keys, so I'm thinking of trying some treasure hunting myself. Only, the monsters scare me, so I can't quite bring myself to go.",
        ["T620"] = "Never did I imagine you would come to this island too, Sir Aleph. Nearly all the soldiers King Alfred sent have been wiped out, and as for me, behold the state I am in. This stone was given to His Majesty's father by the High Elves. Pray, put it to good use.",
        ["T621"] = "We have yet to find the King's sword. What is more, the Hierophant's power seems to grow ever greater. It almost seems he commands the very monsters.",
        ["T622"] = "The surviving soldiers must be awaiting my aid. I must find them quickly, and rescue them.",
        ["T623"] = "I pray they are all still alive.",
        ["T640"] = "Don't come barging into my house.",
        ["T641"] = "Sandler's gone missing, so everyone's worried. If you need something, I'll hear you out later.",
        ["T642"] = "I've got nothing for you. I keep telling you, I'm busy. Don't come back.",
        ["T643"] = "That Sandler, always making Krola worry. Hopeless kid.",
        ["T660"] = "The Hierophant's a good guy, ain't he? Gives us daylight, gives us food. Sure, in return everyone digs holes in the mine. Me? I'm in good with old Celffy, so I'm fine.",
        ["T661"] = "Sandler gave you something, didn't he? Old Celffy will pay plenty for it, so you oughta sell it.",
        ["T662"] = "My big brother Al's a hopeless glutton. Seems he's hunting all over underground, but there's no way there's food down there.",
        ["T663"] = "I take food over to Al's place now and then, but lately he don't even say thanks.",
        ["T664"] = "Whatever the Hierophant's after, it's got something to do with the High Elves. Which means it's gotta be something Seath gave 'em.",
        ["T665"] = "I may stand here all day, but nothing gets past me. Course not. I feed the miners, and they talk.",
        ["T666"] = "You can't get to the east sea right now. The bridge is stuck down. Eh, nothing much there anyway, so no big deal. The pirates' cave is on the west sea. Bet you didn't know that.",
        ["T667"] = "There's somebody called Fai hiding out around the South Village. I know everything, see. Seems he's got something good, but he's got no mind to sell it.",
        ["T668"] = "Sandler told me there's something like a castle near here. You heard too, I bet. Well, whatever. Anyway, find anything good, bring it here.",
        ["T669"] = "Nothing but bad news lately.",
        ["T670"] = "Been to Leon's village? If you've got the Figure of Seath, try going to Fai's place.",
        ["T671"] = "Got any good news?",
        ["T672"] = "That old geezer Celffy took off somewhere. After I was so nice to him, too. Probably went down below to do business with the Hierophant. Bet some sweet deal just fell into his lap.",
        ["T673"] = "With the old man gone, we're short on food too. What a mess. Look how thin I've gotten.",
        ["T674"] = "Dark Elves are a scheming, black-hearted lot anyway.",
        ["T675"] = "Wonder what's happened to everyone. They oughta just find it and come back, but I bet they're dawdling.",
        ["T676"] = "I'd like to go home soon myself.",
        ["T680"] = "I shall not ask who thou art. Slay my brethren, now become vengeful spirits, and wrest from them the black power bestowed by Seath. Ghirah, the Slumbering One, is Seath's shadow. The brighter the light, the darker its shadow.",
        ["T681"] = "Take the warrior's sword with thee.",
        ["T682"] = "Show forth the might of the Chosen One.",
        ["T683"] = "In the beginning, Ghirah and Seath were one, and ever since they became two, they have warred without end. Now must thou wield that sword's power and vanquish the will of Ghirah.",
        ["T685"] = "It seems the Hierophant did take the King's sword after all. Nay, I am certain of it. But even should I wish to take it back, the foes are too many.",
        ["T686"] = "Past the room the Copper Knights guard lies a passage that Harvine III's mage built of the fire element. The power of fire is sealed within.",
        ["T687"] = "And still I see no sign of my soldiers.",
        ["T688"] = "Where could they all have gone?",
        ["T689"] = "It seems a shrine built by the High Elves lies here. A great power is said to be hidden within, but someone has sealed it all in ice.",
        ["T690"] = "Among the merchant's wares is the equipment of the famed High Elf warrior Merel Ul, but the price is dear indeed. I should like to wear it but once.",
        ["T691"] = "What am I to tell King Alfred...",
        ["T692"] = "What am I to tell King Alfred...",
        ["T693"] = "What am I to tell King Alfred...",
        ["T694"] = "Where could they all have gone?",
        ["T695"] = "I'm confident with a sword, but magic, not so much... For want of that, I've never been able to use the special techniques.",
        ["T696"] = "I am told magic is of no use against the Hierophant. No matter, I shall fell him with this proud blade of mine. But where is this Hierophant?",
        ["T697"] = "Where could they all have gone?",
        ["T698"] = "It is time I went as well. Should I lose my life, tell the King this: \"Arnes was brave to the very end.\"",
        ["T705"] = "After a war waged since ages past, the two dragons are grown so evenly matched that neither may touch the other. Thus did Ghirah forge the Moonlight Sword, and Seath the Dark Slayer, and each doth seek a warrior to wield them as their own.",
        ["T706"] = "Those that guard eternal time are the forest, the wind, and the mist. All things are made, and all things perish, by the will of the earth. For the sake of all who call the earth their mother, Ghirah must be struck down.",
        ["T710"] = "I have been wait... for you. In the... the king's power grows ever... Our strength will not last much longer. As soon as... please come.",
        ["T711"] = "Now... undo... and restore the light to your sword. The power of the sword and magic... close the door.",
        ["T712"] = "This place... sealed... lie sleeping. Every seal... will surely be of use.",
        ["T713"] = "Every... on you. The sword... the power of magic. Never forget.",
        ["T715"] = ".....",
        ["T720"] = "Die...",
        ["T725"] = "Accursed one. Know the suffering of my kin.",
        ["T730"] = "Come not near the shrine. Shatter into shards of ice.",
        ["T735"] = "I hear Nora has come to this land. Yet I must guard this place, and fight to the very end.",
        ["T740"] = "This form is the end of a meaningless battle. Surely it was never my master Schudom's wish. In battle, much is lost, and naught is gained. Take what little remains of Schudom's great power.",
        ["T745"] = "Insolent wretch, who defiles my castle. Taste the power of the King of Wind to the full.",
        ["T750"] = "O thou who awakens me from slumber... Offer up all thy energy unto me.",
        ["I120"] = "Al Hunt",
        ["I121"] = "Nora Basil",
        ["I122"] = "Keys Sold & Copied\nWoz Shu",
        ["I123"] = "Rand Ferrer",
        ["I124"] = "Crystal Crafting & Buying\nRadd Bilhem",
        ["I125"] = "Kiel Hunt",
        ["I126"] = "Krola Amgan\nSandler Amgan",
        ["I127"] = "Cliff Lore",
        ["I128"] = "Mack Tabler",
        ["I129"] = "Dalf Vice",
        ["I130"] = "Harris Carvitto",
        ["I131"] = "Theo Budwell\nGigi Budwell",
        ["I132"] = "Karen Shore\nLeon Shore",
        ["I133"] = "1st Guard Post",
        ["I134"] = "2nd Guard Post",
        ["I135"] = "3rd Guard Post",
        ["I136"] = "4th Guard Post",
        ["I137"] = "5th Guard Post",
        ["I138"] = "6th Guard Post",
        ["I139"] = "Ice Demon Grotto",
        ["I140"] = "7th Guard Post",
        ["I141"] = "North Village Entrance",
        ["I142"] = "South Village Entrance",
        ["I143"] = "Finished Unit Storage\nNo Entry",
        ["I144"] = "Central Village Entrance",
        ["I145"] = "Big Mine Passage Entrance",
        ["I146"] = "Great Dungeon",
        ["I147"] = "2nd Dungeon",
        ["I160"] = "Graves of the Soldiers of Harvine III's Royal Guard",
        ["I161"] = "Royal Treasure Hall",
        ["I162"] = "Demon Palace of Fire",
        ["I163"] = "Here lie those who fell to the Guardian Warrior without ever showing their strength to Ghirah.",
        ["I164"] = "Its duty done, the Guardian Warrior tirelessly keeps watch for the next battle.",
        ["I165"] = "Here Harvine III, the King of Wind, began building his castle.",
        ["I166"] = "Castle of Harvine III, Great Ruler of the Northern Continent",
        ["I180"] = "Danger Beyond This Point",
        ["I181"] = "In the name of the Great Mage, entry into this Earth Spirit Cave is forbidden.\n-Schudom",
        ["I182"] = "Galth Fee, Fiend of Darkness\nRot away with the High Elves, and become a phantom of this world...",
        ["I183"] = "An envoy from a foreign land in the east sleeps here forever.",
        ["I184"] = "Most of the soldiers have already perished. No means of leaving the island remain, and we know our return home is an impossible dream.",
        ["I185"] = "In the name of Tzedek, Mage of Fire, I shall cast down King Harvine and take the northern lands into my own hands.",
        ["I186"] = "→\nGigi's House\nSandler",
        ["I187"] = "They are weak to arrows. Aim well and shoot them through.",
        ["I188"] = "Keep Out\nOld Hand",
        ["I189"] = "Cheap Herbs for Sale\nAl Hunt",
        ["I190"] = "←\nTo the South Graveyard\nand Small Mine",
        ["I200"] = "Termite Nest Ahead\nWatch Your Step",
        ["I201"] = "My beloved son, who threw himself into battle for me, Schudom, and fought to the last... Now sleep, and be at peace.",
        ["I203"] = "DANGER - KEEP AWAY\nKRAKEN'S NEST",
        ["I204"] = "DANGER\nNo Way Out Once Inside",
        ["I210"] = "Elf Cave",
        ["I211"] = "One who comes to this land in time shall receive Seath's abundant protection and strike down the black dragon...",
        ["I212"] = "→\nCave of Darkness\nHell on Earth",
        ["I213"] = "Offer all you have to Ghirah, ruler of light and darkness.",
        ["I214"] = "They are the ones who make demons. Do not let your guard down.",
        ["I215"] = "When two warriors stand side by side, the heavy door shall open once more.",
        ["I220"] = "Small Mine Entrance",
        ["I221"] = "Poison Cave\nDANGER",
        ["I230"] = "Fiend's Grave",
        ["I231"] = "Arnes's Body",
        ["I232"] = "Painting of Warriors",
        ["I240"] = "Green Slime\nShuns direct sunlight, and dwells in groups in dark, damp places. A sword does it little harm, but it is weak to fire magic. It has taken antidote water into its body, so touching it cures poison.",
        ["I241"] = "Poison Slime\nOnce the same species as the Green Slime. It has taken the island's poisoned water into its body, so merely touching it will poison you.",
        ["I242"] = "Seagill\nDwells in underground rivers and shallows, and attacks in schools anything that draws near. A savage and grotesque fish.",
        ["I243"] = "Seagill\nAs it grows, the Seagill moves to the deeper waters of the sea. Its savagery grows with it, and it leaps upon its prey.",
        ["I244"] = "Baltail\nAn insect-like monster that always moves in swarms. Its eyes are keen; it closes in from any direction, and strikes its prey from above.",
        ["I245"] = "Baltail\nAn insect-like monster that always moves in swarms. Its eyes are keen; it closes in from any direction, and strikes its prey from above.",
        ["I246"] = "Stingfly\nA venomous variety of Baltail. It attacks by stabbing with a sharp, poisoned stinger.",
        ["I247"] = "Stingfly\nA venomous variety of Baltail. It attacks by stabbing with a sharp, poisoned stinger.",
        ["I248"] = "Fire Elemental\nA fireball inhabited by a fire spirit. It drifts through the air, and has a mind of its own.",
        ["I249"] = "Fire Elemental\nA fireball inhabited by a fire spirit. It drifts through the air, and has a mind of its own.",
        ["I250"] = "Ghost\nA ghost that haunts certain places and drifts toward the living. Its touch paralyzes the body.",
        ["I251"] = "Reiks\nArmored in a chalky shell, it flies through the air. Extremely aggressive and ferocious.",
        ["I254"] = "Head Eater\nAttacks with its great mouth. Venomous stingers line the mouth, so a deep bite may poison you. A different species from those in Verdite, and more dangerous.",
        ["I255"] = "Mogle\nA four-legged beast that evolved on Melanat Island alone. It attacks chiefly with sharp fangs and powerful legs, but at times leaps up and tramples its prey from above.",
        ["I256"] = "S-Knight\nA ferocious creature that will devour anything. Its defense is high, and attacks have no effect at all on its shell.",
        ["I257"] = "Monk-Soldier\nA soldier whose mind is held and wielded by the Hierophant. No will of its own remains, and it feels no pain; it simply closes in and attacks. No helping hand can reach it now.",
        ["I258"] = "Archer\nA pitiful soldier controlled by the Hierophant, like the monk-soldiers. It does nothing but loose arrows at anything that moves.",
        ["I261"] = "Skeleton\nThe bleached bones of one who died alone and unknown, into which the Hierophant breathed an evil soul. Being undead, it rises again and again unless utterly destroyed.",
        ["I262"] = "Termite\nAn insect-like monster resembling a termite. It always moves in numbers, and pours endlessly from its nest.",
        ["I263"] = "Kald\nDwells in the mine caves, and strikes without warning from holes in the walls. It sees in the dark, and attacks from anywhere.",
        ["I264"] = "Scythe\nA reaper created by the Mage of Fire. Highly intelligent, it wields many kinds of magic. A blow from the great scythe in its hands will paralyze.",
        ["I265"] = "Giant Termite\nA queen ant, of sorts, among the termites. It does not move, but attacks anything that comes close with its huge mouth.",
        ["I266"] = "Freeze Ball\nA spirit of ice brimming with hostility, released by Tarn, the ice fiend.",
        ["I267"] = "Log Stalker\nOnce a mere withered tree, it was given a soul by the Hierophant, and now preys upon people as a vicious monster.",
        ["I268"] = "Demon Lord\nA monster created by the Slumbering One. Highly intelligent, it wields advanced magic. Now the Hierophant makes them as well.",
        ["I269"] = "Kraken\nRuler of the northern sea. It drags passing ships beneath the waves with its giant tentacles. While young, it is small and not so powerful.",
        ["I270"] = "Kraken\nRuler of the northern sea. It drags passing ships beneath the waves with its giant tentacles. While young, it is small and not so powerful.",
        ["I271"] = "Earth Elemental\nWields Stone Ball magic.",
        ["I272"] = "Refma\nSpirits of High Elves in whom nothing remains but the dying will to guard the Dark Crystal. They attack all who draw near, and remember not even who they once were.",
        ["I273"] = "Tarn\nKel Fargus, the ice warrior who was once master of the Coliseum, transformed by the Slumbering One and risen again as an ice fiend. It guards the gear it bore in life.",
        ["I274"] = "Copper Knight\nCreated by the King of Wind and the Mage of Fire, it guards the king's treasure.",
        ["I275"] = "Dias Basil\nDrawn to the island as if by the pull of the Slumbering One, in time he took the name of Hierophant. Said to be the mightiest warrior alive, and master of the Coliseum.",
        ["I279"] = "Ghirah\nThe black dragon who opposes the white dragon Seath. He shattered Seath's black crystal, which held his power in check, and made the Moonlight Sword so that the warrior destined to come would face a warrior of his choosing, and from that battle's energy he would rise again.",
        ["I280"] = "Salamander\nMade by Tzedek, the Mage of Fire, as ornaments for his rooms. In time, having lost their master, these ornaments scattered far and wide, and now rain fire magic upon anything that moves.",
        ["I281"] = "Kabeir\nMade by Tzedek, the Mage of Fire, as ornaments for his rooms. In time, having lost their master, these ornaments scattered far and wide, and now rain fire magic upon anything that moves.",
        ["I282"] = "Vilerent\nMade by Tzedek, the Mage of Fire, as ornaments for his rooms. In time, having lost their master, these ornaments scattered far and wide, and now rain fire magic upon anything that moves.",
        ["I284"] = "Cyclops Skeleton\nThe remains of a one-eyed giant made by Schudom, said to be the mightiest mage of the three northern kingdoms. By this giant's power alone did a mage ever become master of the Coliseum.",
        ["I285"] = "Painting of the King\nLong ago, Harvine III painted this picture, imbuing it with magic as he worked, to guard his treasure. It unleashes special attacks on any who approach.",
        ["I286"] = "Mecha Demon Lord\nA mechanized monster with the Demon Lord's powers enhanced. It wields advanced magic. Its eerie eyes set it apart.",
        ["I287"] = "Mecha Termite\nA mechanical monster built in imitation of a creature of Melanat Island. Every trait of the termite is reproduced.",
        ["I288"] = "Mecha Reiks\nA mechanical monster built in imitation of a creature of Melanat Island. Every trait of the Reiks is reproduced.",
        ["I289"] = "Mage's Candlesticks\nLiving candlesticks placed in this room by Tzedek, the Mage of Fire, to test those who come to his palace. They wait still for one who will light all four.",
        ["I290"] = "Bitt\nA lifeless thing Ghirah keeps ever close, as his last line of defense. It can drain magic, and guards Ghirah.",
        ["I291"] = "Refma\nSpirit of a High Elf in whom nothing remains but the dying will to guard the Dark Crystal. It attacks all who draw near, and remembers not even who it once was.",
        ["I304"] = "Celffy Foss\nAn old Dark Elf merchant, well past four hundred years. The Hierophant is his best customer, yet for the villagers he is the only source of everyday goods.",
        ["I305"] = "Celffy Foss\nAn old Dark Elf merchant, well past four hundred years. The Hierophant is his best customer, yet for the villagers he is the only source of everyday goods.",
        ["I306"] = "Raffy Foss\nGrandson of Celffy Foss. Not yet trusted, he deals only in cheap wares.",
        ["I307"] = "Jose Harven\nDescendant of the pirates who once made this island their home. His grandfather told him treasure still lay abandoned here, and he came to find it.",
        ["I310"] = "Nora Basil\nYounger sister of the famed warrior Dias. Hearing that a man calling himself the Hierophant had appeared on this island, she feared for her brother, and crossed to the island alone.",
        ["I313"] = "Meryl\nA fortune-teller who reveals the whole world within her crystal. Beyond that she is a woman, nothing is known of her age or her past.",
        ["I314"] = "Gigi Budwell\nCame to the island with her father. Often ill, she seldom goes outside. She loves to hear Leon's stories.",
        ["I316"] = "Gigi Budwell\nCame to the island with her father. Often ill, she seldom goes outside. She loves to hear Leon's stories.",
        ["I317"] = "Theo Budwell\nOnce a warrior of Verdite, he laid down the sword to earn money for his frail daughter Gigi, and crossed to the island to live as a crystal miner.",
        ["I318"] = "Theo Budwell\nOnce a warrior of Verdite, he laid down the sword to earn money for his frail daughter Gigi, and crossed to the island to live as a crystal miner.",
        ["I319"] = "Theo Budwell\nOnce a warrior of Verdite, he laid down the sword to earn money for his frail daughter Gigi, and crossed to the island to live as a crystal miner.",
        ["I320"] = "Karen Shore\nBorn of the High Elf clan that dwells in secret deep in the forest still. For the sake of Leon, whose blood is half human, she left the village and came to this island with him.",
        ["I321"] = "Leon Shore\nCame to this island with his mother, from the High Elf village deep in the forest. The High Elves have many skilled craftsmen, but his gift surpasses them all. Today he has no equal.",
        ["I322"] = "Leon Shore\nCame to this island with his mother, from the High Elf village deep in the forest. The High Elves have many skilled craftsmen, but his gift surpasses them all. Today he has no equal.",
        ["I323"] = "Al Hunt\nOnce head chef in a certain kingdom, he was thrown out after displeasing the lord of the castle, and came to this island in search of rare ingredients.",
        ["I324"] = "Harris Carvitto\nThe island's oldest resident. A skilled crystal miner until he injured his leg, he now lives apart from the village, keeping watch over his old comrades' graves.",
        ["I325"] = "Fai Fat\nCame to the island with his friend Dias. Some time later he learned that Nora, his friend's sister, had come too. Unable to help her, he thinks only of sending her back to the continent.",
        ["I326"] = "Cliff Lore\nA stubborn but kindhearted young man. Raised in a merchant house that supplies the royal palace of Granatyki, he knows people of high society. He travels to learn the ways of the world, and stopped at the island along the way to work in the mine.",
        ["I327"] = "Rand Ferrer\nA young woodcutter from the deep woods on the outskirts of Verdite. Hardworking and well thought of, he could no longer enter the forest once the monsters came, so he mines crystals on the island and waits for the forest to grow quiet.",
        ["I328"] = "Mack Tabler\nOnce said to be one of the two finest swordsmen in the kingdom of Igrek, he came to doubt the worth of mastering the sword, left the castle's service, and became a crystal miner. Now he works as though he had never known a sword, and is well thought of by all.",
        ["I329"] = "Dalf Vice\nA descendant of one of Harvine III's close retainers. He came to the island to hunt treasure with the King's Key, long handed down in his family. But he lost the key, and now works in the mine.",
        ["I330"] = "Radd Bilhem\nA craftsman who makes his living working crystal. He learned the craft from Leon, but cannot match his work. He makes the Crystal Flasks that hold the water which keeps the island's poison at bay.",
        ["I331"] = "Krola Amgan\nCame to the island with her family from the seaside town of Camy, seeking crystals. Her husband died in an accident, and she now scrapes by looking after the crystal miners.",
        ["I332"] = "Krola Amgan\nCame to the island with her family from the seaside town of Camy, seeking crystals. Her husband died in an accident, and she now scrapes by looking after the crystal miners.",
        ["I333"] = "Sandler Amgan\nRoams underground nearly every day, playing at adventurer. He treasures a key he found by the sea, and keeps wandering deep into the caves in search of rarer things.",
        ["I334"] = "Sandler Amgan\nRoams underground nearly every day, playing at adventurer. He treasures a key he found by the sea, and keeps wandering deep into the caves in search of rarer things.",
        ["I335"] = "Woz Shu\nLed astray by a thief named Gil, he lent his locksmith's skills to Gil's thefts, and could no longer stay in his homeland, so he fled to the island. Too frail for the mine, he keeps a key shop for those who come to hunt treasure.",
        ["I336"] = "Arnes Clyde\nOf Verdian noble birth, and the King's trusted confidant. He sent soldiers to the island to investigate, and when word of them ceased, crossed to the island himself.",
        ["I337"] = "Kiel Hunt\nAl Hunt's younger twin. A farmer back home, he came to the island with his brother. That he lives without doing much work is said to be thanks to some dealings with the Dark Elves.",
        ["I338"] = "Kiel Hunt\nAl Hunt's younger twin. A farmer back home, he came to the island with his brother. That he lives without doing much work is said to be thanks to some dealings with the Dark Elves.",
        ["I339"] = "Karen Shore\nBorn of the High Elf clan that dwells in secret deep in the forest still. For the sake of Leon, whose blood is half human, she left the village and came to this island with him.",
        ["I345"] = "Grave of the Saint\nThe grave of Orwe, the highest priest of the High Elves when they built the shrine. Now only his will remains, seeking to convey the truth.",
        ["I346"] = "Arnes Clyde\nOf Verdian noble birth, and the King's trusted confidant. He sent soldiers to the island to investigate, and when word of them ceased, crossed to the island himself.",
        ["I347"] = "Dragonlord Tree\nIt has stood in this land since long before Ghirah and Seath came, and knows all truth.",
        ["I349"] = "Miria\nCreated by the Slumbering One to seek a human who can wield the Moonlight Sword, she has appeared before warriors many times.",
        ["I360"] = "An ordinary weapon. Nothing special about it.",
        ["I361"] = "An ordinary weapon. Nothing special about it.",
        ["I362"] = "An ordinary weapon. Nothing special about it.",
        ["I363"] = "An ordinary weapon. Nothing special about it.",
        ["I364"] = "An ordinary weapon. Nothing special about it.",
        ["I365"] = "An ordinary weapon. Nothing special about it.",
        ["I366"] = "An ordinary weapon. Nothing special about it.",
        ["I369"] = "Swords of the fire element are known to have been made by several mages. This one is the work of Tzedek, the mage who served King Harvine.",
        ["I370"] = "A sword presented to Harvine III long ago by the king of a foreign land. King Harvine imbued it with the magic of wind.",
        ["I371"] = "This sword bears the earth element. Because it rejects every element but earth, it lowers your defense against magic of the other elements.",
        ["I372"] = "A sword the ice warrior Kel Fargus bore in life, made of condensed ice crystals. Its elements are water and wind. They clash with fire, so fire magic cannot be used while it is equipped, but its power is not to be ignored.",
        ["I373"] = "The sword bestowed by Seath upon the High Elf warrior Merel Ul. It was once destroyed, but was splendidly restored by the elven craftsman Ibil Napaja.",
        ["I374"] = "This sword of the light element is known to the world as a holy sword. In truth, Ghirah made it to call forth a warrior with the energy he needs to rise again. Even so, its power is truly tremendous.",
        ["I375"] = "Forged from the black crystal granted by Seath, the white dragon who opposes Ghirah, this sword holds all four elements: fire, wind, earth and water. It is the sword most deadly to Ghirah, who is of the light element. Had Merel Ul not fallen to Galth Fee, it would have come into the world far sooner.",
        ["I376"] = "An ordinary weapon. Nothing special about it.",
        ["I377"] = "An ordinary weapon. Nothing special about it.",
        ["I381"] = "An ordinary helm. Nothing special about it.",
        ["I382"] = "An ordinary helm. Nothing special about it.",
        ["I383"] = "An ordinary helm. Nothing special about it.",
        ["I384"] = "It looks like nothing more than an old crown, but a strange will is sealed within. While it is worn, the time between your spells grows longer.",
        ["I385"] = "Looses lightning at any enemy standing before you, aiding its wearer. This is also the helm once worn by the fiend Galth Fee.",
        ["I386"] = "The helm bestowed by Seath upon the High Elf warrior Merel Ul. It heals its wearer's wounds unbidden.",
        ["I388"] = "Ordinary armor. Nothing special about it.",
        ["I389"] = "Ordinary armor. Nothing special about it.",
        ["I390"] = "Armor the ice warrior Kel Fargus wore in life, made of condensed ice crystals. It bears the attributes of water and wind, and its defense against fire is remarkable.",
        ["I391"] = "Armor worn by the fiend Galth Fee. When worn, all around you turns to darkness. It shuns light, so light magic cannot be used while it is equipped.",
        ["I392"] = "The armor bestowed by Seath upon the High Elf warrior Merel Ul.",
        ["I394"] = "An ordinary shield. Nothing special about it.",
        ["I395"] = "An ordinary shield. Nothing special about it.",
        ["I396"] = "A shield filled with the holy power of moonlight. It heals its wearer's wounds unbidden.",
        ["I397"] = "A shield the ice warrior Kel Fargus bore in life, made of condensed ice crystals. It bears the attributes of water and wind, and its defense against fire is remarkable.",
        ["I398"] = "One of the fiend Galth Fee's pieces of equipment, a shield that guards its bearer while draining their vitality.",
        ["I399"] = "The shield bestowed by Seath upon the High Elf warrior Merel Ul.",
        ["I401"] = "Ordinary gloves. Nothing special about them.",
        ["I402"] = "Ordinary gloves. Nothing special about them.",
        ["I403"] = "Ordinary gloves. Nothing special about them.",
        ["I404"] = "One of the fiend Galth Fee's pieces of equipment. Its defense is high, but the moment you put it on, your weapon feels heavy, and the time between attacks grows longer.",
        ["I405"] = "The gauntlets bestowed by Seath upon the High Elf warrior Merel Ul. They guard against poison.",
        ["I407"] = "Ordinary boots. Nothing special about them.",
        ["I408"] = "Ordinary boots. Nothing special about them.",
        ["I409"] = "Greaves made chiefly of silver, the holy metal. They guard against poison.",
        ["I410"] = "One of the fiend Galth Fee's pieces of equipment. Its defense is high, but the moment you put it on, your feet grow heavy, and you can no longer move quickly.",
        ["I411"] = "The greaves bestowed by Seath upon the High Elf warrior Merel Ul.",
        ["I413"] = "While worn, lowers your chance of being poisoned.",
        ["I414"] = "While worn, a Figure of Seath will die in your stead.",
        ["I415"] = "A work of the elven craftsman Ibil Napaja. Wearing it raises your attack power.",
        ["I416"] = "The ring of Schudom, the mage said to wield the greatest magic of the three northern kingdoms. Wearing it raises your magic power.",
        ["I417"] = "A collar once worn by a one-eyed giant of the island as the wellspring of its strength. Wearing it raises your strength.",
        ["I418"] = "Given to the High Elves by Seath. While worn, it raises your defense against monsters' magic, and lets you shake off ailments quickly.",
        ["I419"] = "While worn, ordinary arrows become arrows of light, making the Bow and Arbalest far deadlier weapons.",
        ["I427"] = "A map left by the pirates who were once on this island.",
        ["I428"] = "A map of the island, centered on the mines.",
        ["I429"] = "A map of the underground realm where the Hierophant dwells.",
        ["I431"] = "Cures ailments. It has no effect on poison, however.",
        ["I432"] = "Restores a weary spirit.",
        ["I433"] = "Raises your base magic power by one.",
        ["I434"] = "Heals a wounded body.",
        ["I435"] = "Purges poison from the body, and heals wounds a little.",
        ["I436"] = "Cures all ailments, and heals both wounds and spirit.",
        ["I437"] = "Heals a wounded body.",
        ["I438"] = "Restores a weary spirit.",
        ["I439"] = "Cures all ailments.",
        ["I440"] = "Cures all ailments, and heals both wounds and spirit.",
        ["I442"] = "Lets you draw healing water and carry it with you.",
        ["I443"] = "Guards whoever bears Seath's Tear, and gives itself in their place to save their life.",
        ["I444"] = "For a time, lets you discern hidden doors.",
        ["I445"] = "A mirror that reveals all truth: of people, of monsters, of places, and more. It is said there are seven such mirrors in the world.",
        ["I446"] = "For a time, lets you cast magic without spending MP.",
        ["I447"] = "Raises your magic power for a time.",
        ["I448"] = "Played beside the Pillars of Wind left by Harvine III, it calls forth a bridge of wind to guide you.",
        ["I449"] = "Rings out when used near a crystal, helping you seek crystals out.",
        ["I450"] = "Learn one fire spell.",
        ["I451"] = "Learn one water spell.",
        ["I452"] = "Learn one earth spell.",
        ["I453"] = "Learn one wind spell.",
        ["I454"] = "Learn one light spell.",
        ["I455"] = "The crystal Seath bestowed upon the High Elves. If you wish to defeat Ghirah, forge a weapon from it.",
        ["I456"] = "An ordinary item. Nothing special about it.",
        ["I457"] = "An ordinary item. Nothing special about it.",
        ["I459"] = "The key to the Dark Crystal's pedestal.",
        ["I460"] = "The key to the pirates' room and the pirates' treasure chests.",
        ["I461"] = "The key to the entrance of the cave behind the waterfall.",
        ["I462"] = "The key to the jail that the Hierophant controls.",
        ["I463"] = "Used in the keyholes on both sides of the monk-soldiers' doors. It can also be removed again.",
        ["I464"] = "The key to the doors of Harvine III's castle ruins and the treasure chests inside.",
        ["I465"] = "The key that releases the water of the fountain Seath granted. When all three stones are in place, water springs from the goddess statue in the center of the fountain.",
        ["I466"] = "Since long ago, mages from many lands have hidden things on this island. Their treasure chests lie untouched still, and this is the key to such chests.",
        ["I467"] = "A key used for ordinary treasure chests.",
        ["I468"] = "A key made for one special treasure chest alone. Where that all-important chest might be, however, no one knows...",
        ["I469"] = "The key to the entrance of the shrine the High Elves built. The shrine has two keys, and its door will not open unless the pedestals holding them are turned so the pictures match.",
        ["I471"] = "Lets you warp to the guidepost pedestal where the Moon Key is set.",
        ["I472"] = "Lets you warp to the guidepost pedestal where the Star Key is set.",
        ["I473"] = "Lets you warp to the guidepost pedestal where the Sun Key is set.",
        ["I474"] = "The key paired with the Moon Gate.",
        ["I475"] = "The key paired with the Star Gate.",
        ["I476"] = "The key paired with the Sun Gate.",
        ["I477"] = "Arrows for the Bow. Naturally, they are no use on their own.",
        ["I478"] = "Bolts needed to use the Arbalest. They bear the fire, wind, earth and water elements.",
        ["I510"] = "West Seaside",
        ["I511"] = "Pirates' Cave",
        ["I512"] = "Old Hand's Cave",
        ["I513"] = "The Mage's Lighthouse",
        ["I514"] = "North Village",
        ["I515"] = "South Village",
        ["I516"] = "Small Mine",
        ["I517"] = "North Graveyard",
        ["I518"] = "1st Guard Post",
        ["I519"] = "2nd Guard Post",
        ["I520"] = "3rd Guard Post",
        ["I521"] = "4th Guard Post",
        ["I522"] = "5th Guard Post",
        ["I523"] = "6th Guard Post",
        ["I524"] = "Ruins of King\nHarvine's Castle",
        ["I525"] = "Termite Nest",
        ["I526"] = "Common Graveyard",
        ["I527"] = "Soldiers' Graveyard",
        ["I528"] = "Window of Homesickness",
        ["I529"] = "Elf Graveyard",
        ["I530"] = "East Village",
        ["I531"] = "Elf Shrine",
        ["I532"] = "Seath's Fountain",
        ["I533"] = "Demon Palace of Fire",
        ["I534"] = "Ice Demon Grotto",
        ["I535"] = "Fiend's Grave",
        ["I536"] = "South Graveyard",
        ["I537"] = "Big Mine",
        ["I538"] = "Poison Cave",
        ["I539"] = "Cave of Darkness",
        ["I540"] = "Elf Cave",
        ["I541"] = "Cave of the Earth Spirit",
        ["I542"] = "Lookout Post",
        ["I543"] = "Hierophant's Coliseum",
        ["I544"] = "Warriors' Graveyard",
        ["I545"] = "Laboratory",
        ["I546"] = "Village of the Wind",
        ["I547"] = "East Seaside",
    };

    /// <summary>Menu-font strings in GAME.EXE: (text address, new text).</summary>
    public static readonly (uint Address, string Text)[] Strings =
    [
        (0x80064CF4, "Use Item"),   // menu
        (0x80064D10, "Use Magic"),   // menu
        (0x80064D2C, "Equip"),   // menu
        (0x80064D48, "Attack/Defense"),   // menu
        (0x80064D64, "Discard"),   // menu
        (0x80064D80, "System"),   // menu
        (0x80064D9C, "Options"),   // menu
        (0x80064DB8, "Back"),   // menu
        (0x80064E0C, "System"),   // menu
        (0x80064E28, "Load"),   // menu
        (0x80064E44, "Quit Game"),   // menu
        (0x80064E60, "Back"),   // menu
        (0x80064E7C, "Save"),   // menu
        (0x80064E98, "Load"),   // menu
        (0x80064EB4, "Quit Game"),   // menu
        (0x80064F40, "Options"),   // menu
        (0x80064F5C, "Sound Effects"),   // menu
        (0x80064F78, "Music"),   // menu
        (0x80064F94, "Display HP/MP"),   // menu
        (0x80064FB0, "Display Compass"),   // menu
        (0x80064FCC, "Display Items"),   // menu
        (0x80064FE8, "Walking Effect"),   // menu
        (0x80065004, "Back"),   // menu
        (0x80065074, "Shop"),   // menu
        (0x80065090, "Buy"),   // menu
        (0x800650AC, "Sell"),   // menu
        (0x800650C8, "Back"),   // menu
        (0x800651A8, "Locksmith"),   // menu
        (0x800651C4, "Buy"),   // menu
        (0x800651E0, "Make a Copy"),   // menu
        (0x800651FC, "Back"),   // menu
        (0x800652F8, "Appraisal"),   // menu
        (0x80065314, "Crystal Shop"),   // menu
        (0x8006542C, "New"),   // menu
        (0x80065448, "Load"),   // menu
        (0x80065674, "Weapon"),   // label
        (0x8006568C, "Magic"),   // label
        (0x800656A4, "Shield"),   // label
        (0x800656BC, "Head"),   // label
        (0x800656D4, "Body"),   // label
        (0x800656EC, "Arms"),   // label
        (0x80065704, "Feet"),   // label
        (0x8006571C, "Accessory 1"),   // label
        (0x80065734, "Accessory 2"),   // label
        (0x8006574C, "■ Button"),   // label
        (0x8006577C, "Remove"),   // label
        (0x80065794, "Experience"),   // label
        (0x800657AC, "Level"),   // label
        (0x800657C4, "Attack"),   // label
        (0x800657DC, "Defense"),   // label
        (0x800657F4, "Condition"),   // label
        (0x8006580C, "Paralysis"),   // label
        (0x80065824, "Curse"),   // label
        (0x8006583C, "Slow"),   // label
        (0x80065854, "Poison"),   // label
        (0x8006586C, "Dark"),   // label
        (0x80065884, "Good"),   // label
        (0x8006589C, "Gold"),   // label
        (0x800658B4, "Checking memory card"),   // label
        (0x800658CC, "Please wait"),   // label
        (0x800658E4, "Cannot save"),   // label
        (0x800658FC, "Check the memory card"),   // label
        (0x80065914, "Not formatted"),   // label
        (0x8006592C, "Format the card?"),   // label
        (0x80065944, "Cannot save if the"),   // label
        (0x8006595C, "card is not formatted"),   // label
        (0x80065974, "Cannot save data"),   // label
        (0x8006598C, "on this card"),   // label
        (0x800659A4, "Cannot load"),   // label
        (0x800659BC, "Saving"),   // label
        (0x800659D4, "Not enough space"),   // label
        (0x800659EC, "Loading"),   // label
        (0x80065A04, "Remove the CD"),   // label
        (0x80065A1C, "and turn off the power"),   // label
        (0x80065A34, "Data error!"),   // label
        (0x80065A4C, "Cannot save"),   // label
        (0x80065A64, "without a memory card"),   // label
        (0x80065A7C, "Memory card is full"),   // label
        (0x80065A94, "Insert a new card"),   // label
        (0x80065B24, "Dagger"),   // name
        (0x80065B3C, "Short Sword"),   // name
        (0x80065B54, "Knight Sword"),   // name
        (0x80065B6C, "Morning Star"),   // name
        (0x80065B84, "Battle Hammer"),   // name
        (0x80065B9C, "Bastard Sword"),   // name
        (0x80065BB4, "Crescent Axe"),   // name
        (0x80065BFC, "Flame Sword"),   // name
        (0x80065C14, "Shiden"),   // name
        (0x80065C2C, "Spider"),   // name
        (0x80065C44, "Ice Blade"),   // name
        (0x80065C5C, "Merel Ul's Sword"),   // name
        (0x80065C74, "Moonlight Sword"),   // name
        (0x80065C8C, "Dark Slayer"),   // name
        (0x80065CA4, "Bow"),   // name
        (0x80065CBC, "Arbalest"),   // name
        (0x80065D1C, "Iron Mask"),   // name
        (0x80065D34, "Knight Helm"),   // name
        (0x80065D4C, "Great Helm"),   // name
        (0x80065D64, "Blood Crown"),   // name
        (0x80065D7C, "Lightning Helm"),   // name
        (0x80065D94, "Merel Ul's Helm"),   // name
        (0x80065DC4, "Breastplate"),   // name
        (0x80065DDC, "Knight Plate"),   // name
        (0x80065DF4, "Ice Armor"),   // name
        (0x80065E0C, "Abyss Armor"),   // name
        (0x80065E24, "Merel Ul's Armor"),   // name
        (0x80065E54, "Leather Shield"),   // name
        (0x80065E6C, "Large Shield"),   // name
        (0x80065E84, "Moon Guard"),   // name
        (0x80065E9C, "Crystal Guard"),   // name
        (0x80065EB4, "Skull Shield"),   // name
        (0x80065ECC, "Merel Ul's Shield"),   // name
        (0x80065EFC, "Iron Gloves"),   // name
        (0x80065F14, "Stone Hands"),   // name
        (0x80065F2C, "Silver Arms"),   // name
        (0x80065F44, "Demon Hands"),   // name
        (0x80065F5C, "Ruinous Gloves"),   // name
        (0x80065F8C, "Iron Boots"),   // name
        (0x80065FA4, "Leg Guarders"),   // name
        (0x80065FBC, "Silver Boots"),   // name
        (0x80065FD4, "Death Walkers"),   // name
        (0x80065FEC, "Ruinous Boots"),   // name
        (0x8006601C, "Scorpion Bracelet"),   // name
        (0x80066034, "Seath's Tear"),   // name
        (0x8006604C, "Merel Ul's Bracelet"),   // name
        (0x80066064, "Earth Spirit Ring"),   // name
        (0x8006607C, "Cyclops Collar"),   // name
        (0x80066094, "Forest Mist Amulet"),   // name
        (0x800660AC, "Lightwave Ring"),   // name
        (0x8006616C, "Pirates' Map"),   // name
        (0x80066184, "Crystal Miner's Map"),   // name
        (0x8006619C, "Hierophant's Map"),   // name
        (0x800661B4, "Gold Coin"),   // name
        (0x800661CC, "Blood Stone"),   // name
        (0x800661E4, "Moon Stone"),   // name
        (0x800661FC, "Verdite"),   // name
        (0x80066214, "Herb"),   // name
        (0x8006622C, "Antidote Herb"),   // name
        (0x80066244, "Dragonlord Fruit"),   // name
        (0x8006625C, "Healing Water"),   // name
        (0x80066274, "Healing Water"),   // name
        (0x8006628C, "Healing Water"),   // name
        (0x800662A4, "Healing Water"),   // name
        (0x800662BC, "Poison Water"),   // name
        (0x800662D4, "Crystal Flask"),   // name
        (0x800662EC, "Figure of Seath"),   // name
        (0x80066304, "Phantom Staff"),   // name
        (0x8006631C, "Mirror of Truth"),   // name
        (0x80066334, "Seath's Plume"),   // name
        (0x8006634C, "Heavy Magic Needle"),   // name
        (0x80066364, "Flute of Wind"),   // name
        (0x8006637C, "Whispering Bell"),   // name
        (0x80066394, "Fire Crystal"),   // name
        (0x800663AC, "Water Crystal"),   // name
        (0x800663C4, "Earth Crystal"),   // name
        (0x800663DC, "Wind Crystal"),   // name
        (0x800663F4, "Light Crystal"),   // name
        (0x8006640C, "Dark Crystal"),   // name
        (0x80066424, "Crystal"),   // name
        (0x8006643C, "Crystal Shard"),   // name
        (0x80066454, "Unused"),   // name
        (0x8006646C, "Elf Key"),   // name
        (0x80066484, "Pirates' Key"),   // name
        (0x8006649C, "Skull Key"),   // name
        (0x800664B4, "Jail Key"),   // name
        (0x800664CC, "Monk-Soldier's Key"),   // name
        (0x800664E4, "King's Key"),   // name
        (0x800664FC, "Dragon Stone"),   // name
        (0x80066514, "Mage's Key"),   // name
        (0x8006652C, "Silver Key"),   // name
        (0x80066544, "Gold Key"),   // name
        (0x8006655C, "Shrine Key"),   // name
        (0x80066574, "Warrior's Arm"),   // name
        (0x8006658C, "Moon Gate"),   // name
        (0x800665A4, "Star Gate"),   // name
        (0x800665BC, "Sun Gate"),   // name
        (0x800665D4, "Moon Key"),   // name
        (0x800665EC, "Star Key"),   // name
        (0x80066604, "Sun Key"),   // name
        (0x8006661C, "Crystal Arrow"),   // name
        (0x80066634, "Elf Bolts"),   // name
        (0x8006664C, "Last Gate"),   // name
        (0x80066664, "Waterfall"),   // name
        (0x8006667C, "Stone"),   // name
        (0x80066694, "Earth Wave"),   // name
        (0x800666AC, "Meteor"),   // name
        (0x800666C4, "Wind Cutter"),   // name
        (0x800666DC, "Ice Storm"),   // name
        (0x800666F4, "Freeze"),   // name
        (0x8006670C, "Fireball"),   // name
        (0x80066724, "Fire Wall"),   // name
        (0x8006673C, "Fire Storm"),   // name
        (0x80066754, "Flame"),   // name
        (0x8006676C, "Lightning Bolt"),   // name
        (0x80066784, "Flash"),   // name
        (0x8006679C, "Seath"),   // name
        (0x800667B4, "Dispoison"),   // name
        (0x800667CC, "Resist Fire"),   // name
        (0x800667E4, "Earth Heal"),   // name
        (0x800667FC, "Missile Shield"),   // name
        (0x80066814, "Light"),   // name
        (0x8006682C, "Bless"),   // name
    ];

    /// <summary>The short messages in play, in the page's order (the workbook's
    /// Messages in play sheet, H0-H35): shared as "hud:" + the number, and drawn by
    /// the Text mod in the menu font, at most 127 pixels wide. H21 follows the
    /// amount ("36 Gold").</summary>
    public static readonly string[] Hud =
    [
        "Level up",                 // H0  LEVEL UP
        "Magic learned",            // H1  MAGIC LEARNED
        "Locked",                   // H2  LOCKED
        "Sealed",                   // H3  SEALED
        "Wrong key",                // H4  WRONG KEY
        "Nothing here",             // H5  NOTHING THERE
        "Nothing inside",           // H6  EMPTY
        "Dead",                     // H7  DEAD
        "A broken sword",           // H8  A BROKEN SWORD
        "Bones",                    // H9  BONES
        "A warrior's grave",        // H10 A WARRIOR'S GRAVE
        "Theo's body",              // H11 TEO'S BODY
        "Crystal miner's body",     // H12 A DEAD CRYSTAL-MINER
        "A Verdian soldier",        // H13 A SOLDIER OF VERDITE
        "A tree",                   // H14 A TREE
        "Dragonlord Tree",          // H15 DRAGON GRASS
        "Guidepost pedestal",       // H16 A GUIDEPOST
        "Pillar of Wind",           // H17 THE PILLAR OF WIND
        "Can't carry any more",     // H18 CAN'T CARRY ANY MORE
        "You are not worthy",       // H19 NOT ACCEPTED
        "Nothing happens",          // H20 NOTHING HAPPENS
        "gold obtained",            // H21 GOLD
        "Drew some water",          // H22 DREW SOME WATER
        "A gravestone",             // H23 A GRAVE
        "A broken shovel",          // H24 A BROKEN SHOVEL
        "Elf's grave",              // H25 THE ELF'S GRAVE
        "Crystal miner's grave",    // H26 THE MINER'S GRAVE
        "Soldier's grave",          // H27 THE SOLDIER'S GRAVE
        "Harris' grave",            // H28 HARRIS'S GRAVE
        "Samurai's grave",          // H29 THE SAMURAI'S GRAVE
        "Mage's Candlestick",       // H30 THE MAGICIAN'S LAMP
        "Strength increased",       // H31 STRENGTH GAINED
        "Magic increased",          // H32 MAGIC GAINED
        "Culture tank",             // H33 A CULTURE MEDIUM
        "The effect wore off",      // H34 LOST THE EFFECT
        "A candlestick",            // H35 A LAMP
    ];

    /// <summary>Labels the game builds in code (STR POWER, YES, ...), by their US text:
    /// rewritten as they are drawn, at most 11 letters.</summary>
    public static readonly Dictionary<string, string> Coded = new()
    {
        ["STR POWER"] = "Strength",
        ["MAG POWER"] = "Magic",
        ["SLASH"] = "Slash",
        ["CHOP"] = "Strike",
        ["STAB"] = "Pierce",
        ["HOLY MAGIC"] = "Holy Magic",
        ["FIRE MAGIC"] = "Fire Magic",
        ["EARTH MAGIC"] = "Earth Magic",
        ["WIND MAGIC"] = "Wind Magic",
        ["WATER MAGIC"] = "Water Magic",
        ["POISON"] = "Poison",
        ["DARK MAGIC"] = "Dark Magic",
        ["YES"] = "Yes",
        ["NO"] = "No",
        ["ON"] = "On",
        ["OFF"] = "Off",
        ["CRYSTAL"] = "Crystal",
        ["GOLD"] = "Gold",
        ["NUMBER"] = "Quantity",
        ["RETURN"] = "Back",
        ["QUIT"] = "Quit",
    };

    /// <summary>The Encyclopedia's own English -> this translation, shared through the
    /// Text mod's string table as "encyclopedia:" + the English.</summary>
    public static readonly Dictionary<string, string> Encyclopedia = new()
    {
        ["Sigill"] = "Seagill",
        ["Deep-Sea Sigill"] = "Deep Seagill",
        ["Fire-Elemental"] = "Fire Elemental",
        ["Ghosts"] = "Ghost",
        ["Necron's Soldiers"] = "Monk-Soldier",
        ["Archers"] = "Archer",
        ["Queen Termite"] = "Giant Termite",
        ["The Picture of a King"] = "Painting of the King",
        ["Flame Spirit"] = "Kabeir",
        ["Psythe"] = "Scythe",
        ["Poison Baltail"] = "Stingfly",
        ["Earth-Elemental"] = "Earth Elemental",
        ["Billent"] = "Vilerent",
        ["Dias Bagil"] = "Dias Basil",
        ["Mecha-Termite"] = "Mecha Termite",
        ["Mecha-Reiks"] = "Mecha Reiks",
        ["Mecha-Demon Lord"] = "Mecha Demon Lord",
        ["Bitt"] = "Bit",
        ["Guyra"] = "Ghirah",
        ["Nola Bagil"] = "Nora Basil",
        ["Fai Fadlin"] = "Fai Fat",
        ["Mark Wozz"] = "Woz Shu",
        ["Earnest Clyde"] = "Arnes Clyde",
        ["Radd Bilheim"] = "Radd Bilhem",
        ["Krola Amgun"] = "Krola Amgan",
        ["Sandler Amgun"] = "Sandler Amgan",
        ["Kehl Hunt"] = "Kiel Hunt",
        ["Teo Budwell"] = "Theo Budwell",
        ["Harris Carvitto"] = "Harris Carbite",
        ["The Grave of a Saint"] = "Grave of the Saint",
        ["Dragon Grass"] = "Dragonlord Tree",
        ["David Tabler"] = "Mack Tabler",
        ["Skeleton of One-Eyed Giant"] = "Cyclops Skeleton",
        ["The Pirate's Cave"] = "Pirates' Cave",
        ["The Cave of Old-Hand"] = "Old Hand's Cave",
        ["The North Village"] = "North Village",
        ["The South Village"] = "South Village",
        ["A Small Mine"] = "Small Mine",
        ["The North Graveyard"] = "North Graveyard",
        ["Base No. 1"] = "1st Guard Post",
        ["The South Graveyard"] = "South Graveyard",
        ["The Underworld Prison No. 1"] = "1st Dungeon",
        ["Base No. 2"] = "2nd Guard Post",
        ["The Ruins of King Harvine's Castle"] = "Ruins of King Harvine's Castle",
        ["The Termite's Nest"] = "Termite Nest",
        ["The Window of Homesickness"] = "Window of Homesickness",
        ["The Village of the Wind"] = "Village of the Wind",
        ["Base No. 3"] = "3rd Guard Post",
        ["The Cemetery"] = "Common Graveyard",
        ["The Soldier's Cemetery"] = "Soldiers' Graveyard",
        ["The Elf Graveyard"] = "Elf Graveyard",
        ["The East Village"] = "East Village",
        ["The East Seaside"] = "East Seaside",
        ["Base No. 4"] = "4th Guard Post",
        ["The Elf Shrine"] = "Elf Shrine",
        ["The Magic Palace of Fire"] = "Demon Palace of Fire",
        ["The Magic Palace of Ice"] = "Ice Demon Grotto",
        ["The Underworld Prison No. 2"] = "2nd Dungeon",
        ["Royal Treasure House"] = "Royal Treasure Hall",
        ["Base No. 5"] = "5th Guard Post",
        ["The Big Mine"] = "Big Mine",
        ["The Poisoned Cave"] = "Poison Cave",
        ["The Cave of the Darkside"] = "Cave of Darkness",
        ["The Elf Cave"] = "Elf Cave",
        ["The Cave of The Earth Soul"] = "Cave of the Earth Spirit",
        ["The Lookout"] = "Lookout Post",
        ["Base No. 6"] = "6th Guard Post",
        ["Necron's Coliseum"] = "Hierophant's Coliseum",
        ["The Research Office"] = "Laboratory",
        ["Space for Completion"] = "Finished Unit Storage",
        ["Guyra's Dimension"] = "Ghirah's Dimension",
        ["The Demon's Grave"] = "Fiend's Grave",
        ["The Warrior's Graveyard"] = "Warriors' Graveyard",
        ["ENCYCLOPEDIA"] = "Encyclopedia",
        ["ENEMIES"] = "Enemies",
        ["PEOPLE"] = "People",
        ["PLACES"] = "Places",
        ["EQUIPMENT"] = "Equipment",
        ["ITEMS"] = "Items",
        ["WEAPON"] = "Weapon",
        ["SHIELD"] = "Shield",
        ["HEAD"] = "Head",
        ["BODY"] = "Body",
        ["ARMS"] = "Arms",
        ["FEET"] = "Feet",
        ["ACCESSORY"] = "Accessory",
    };

    /// <summary>The Encyclopedia's own place cards, by their English: "encyclopedia-card:" + it.</summary>
    public static readonly Dictionary<string, string> EncyclopediaCards = new()
    {
        ["Kraken's Nest"] = "Kraken's Nest",
        ["Royal Treasure House"] = "Royal Treasure Hall",
        ["The Space For Completion"] = "Finished Unit Storage\nNo Entry",
        ["Guyra's Dimension"] = "Ghirah's Dimension",
    };
}
