gt.quest = {
    pluralName: 'Quests',
    type: 'quest',
    blockTemplate: null,
    linkTemplate: null,
    index: {},
    partialIndex: {},
    genreIndex: null,
    version: 2,
    browse: [
        { type: 'group', prop: 'section' },
        { type: 'group', prop: 'category' },
        { type: 'group', prop: 'genre' },
        { type: 'sort', prop: 'sort' }
    ],
    // This is a hack for generating urls
    loreModule: {
        type: "questlore",
        version: 1,
        index: {},
        cache: function(data) {
            gt.quest.loreModule.index[data.questlore.id] = data.questlore;
        },
    },

    initialize: function(data) {
        gt.quest.blockTemplate = doT.template($('#block-quest-template').text());
        gt.quest.linkTemplate = doT.template($('#link-quest-template').text());
        gt.quest.lorePageTemplate = doT.template($('#page-quest-lore-template').text());
    },

    cache: function(data) {
        gt.quest.index[data.quest.id] = data.quest;
    },

    bindEvents: function($block, data) {
        gt.display.alternatives($block, data);
    },

    getViewModel: function(quest, data) {
        var view = {
            obj: quest,
            id: quest.id,
            type: 'quest',
            name: quest.name,
            patch: gt.formatPatch(quest.patch),
            template: gt.quest.blockTemplate,
            settings: 1,
            icon: '../files/icons/event/' + quest.eventIcon + '.png',
            
            genreIcon: gt.quest.getGenreIcon(quest.genre),
            interval: quest.interval ? gt.util.pascalCase(quest.interval) : null,
            issuer: quest.issuer ? gt.model.partial(gt.npc, quest.issuer) : null,
            beast: quest.beast,
            lvl: 1,
            location: quest.location
        };

        var genre = gt.quest.genreIndex[quest.genre];
        view.genre = genre.name || "Adventurer Quests";
        view.category = genre.category;
        view.section = genre.section;
        view.subheader = (view.interval ? (view.interval + ' ') : '') +  view.section;

        if (view.issuer)
            view.byline = view.issuer.name + ', ' + view.location;
        else
            view.byline = view.location;

        if (data) {
            if (quest.target)
                view.target = gt.model.partial(gt.npc, quest.target);

            if (quest.icon)
                view.fullIcon = '../files/icons/quest/' + quest.icon + '.png';

            if (quest.involved)
                view.involved = gt.model.partialList(gt.npc, quest.involved);

            if (quest.next)
                view.next = gt.model.partialList(gt.quest, quest.next);

            if (quest.usedItems)
                view.usedItems = gt.model.partialList(gt.item, quest.usedItems);

            if (quest.reward) {
                view.reward = {
                    xp: quest.reward.xp,
                    gil: quest.reward.gil,
                    emote: quest.reward.emote,
                    gcseal: quest.reward.gcseal,
                    reputation: quest.reward.reputation,
                    aetherCurrent: quest.reward.aetherCurrent
                };

                if (quest.reward.job)
                    view.reward.job = gt.jobs[quest.reward.job];

                if (quest.reward.instance)
                    view.reward.instance = gt.model.partial(gt.instance, quest.reward.instance);

                if (quest.reward.action)
                    view.reward.action = gt.model.partial(gt.action, quest.reward.action);

                if (quest.reward.items) {
                    var items = [];
                    var optional = [];
                    for (var i = 0; i < quest.reward.items.length; i++) {
                        var itemReward = quest.reward.items[i];
                        var item = gt.model.partial(gt.item, itemReward.id);
                        if (item)
                            (itemReward.one ? optional : items).push({ num: itemReward.num, item: item, hq: itemReward.hq });
                    }
                    view.reward.items = items;
                    if (optional.length)
                        view.reward.optional = optional;
                }
            }

            if (quest.reqs) {
                view.reqs = {
                    beastrank: quest.reqs.beastrank,
                    house: quest.reqs.house,
                    gcrank: quest.reqs.gcrank,
                    mount: quest.reqs.mount
                };

                if (quest.reqs.gc)
                    view.reqs.gc = gt.grandCompanies[quest.reqs.gc];

                if (quest.reqs.quests) {
                    view.reqs.quests = gt.model.partialList(gt.quest, quest.reqs.quests);
                    view.reqs.questsType = quest.reqs.questsType;
                }

                if (quest.reqs.instances)
                    view.reqs.instances = gt.model.partialList(gt.instance, quest.reqs.instances);

                if (quest.reqs.jobs) {
                    if (quest.reqs.jobs.length > 1) {
                        var jobs = [];
                        for (var i = 0; i < quest.reqs.jobs.length; i++) {
                            var jobRequirement = quest.reqs.jobs[i];
                            jobs.push('Lv. ' + jobRequirement.lvl + ' ' + gt.jobCategories[jobRequirement.id].name);
                        }
                        view.reqs.jobs = jobs.join(', ');
                    }
                    view.lvl = quest.reqs.jobs[0].lvl;
                }
            }
        }

        return view;
    },

    blockLoaded: function($block, view){
        function loadLorePage(lore){
            $(".lore-page", $block).html(gt.quest.lorePageTemplate(gt.quest.getLoreViewModel(lore)));
            gt.display.collapsible($block);
            gt.display.draggable($block);
            gt.display.omniscroll($block);
            $(".copyright-read-check", $block).change(gt.quest.loreAudioCopyrightChecked);
        }

        if (gt.quest.loreModule.index[view.id]){
            loadLorePage(gt.quest.loreModule.index[view.id]);
        } else {
            gt.core.fetch(this.loreModule, [view.id], function (results){
                var obj = results[0];
                if (obj.error){
                    $block.nearest(".lore-page").html("Lore loading failed! Re-open this block to retry~")
                } else {
                    loadLorePage(obj.questlore);
                }
            })
        }
    },

    loreAudioCopyrightChecked: function (e){
        var $page = e.target.closest(".lore-page");
        var $audios = $(".dialogue-voice", $page);
        if (e.target.checked){
            $audios.removeClass("copyright-marked-up");
        } else {
            $audios.addClass("copyright-marked-up");
        }
    },

    getLoreViewModel: function(lore){
        view = {
            objectives: lore.objectives,
            journal: lore.journal,
        };

        view.dialogue = [];
        var lastName = null;
        for (var i = 0; i < lore.dialogue.length; i++) {
            var line = lore.dialogue[i];
            if (lastName != line.name)
                view.dialogue.push({ type: 'speaker', text: line.name });
            view.dialogue.push({ type: 'dialogue-line', text: line.text });
            lastName = line.name;
        }

        lastName = null;
        if (lore.cutscenes) {
            view.cutscenes = [];
            for (var i = 0; i < lore.cutscenes.length; i++) {
                var cutscene = lore.cutscenes[i];
                var viewCut = [];
                for (var j = 0; j < cutscene.length; j++) {
                    var line = cutscene[j];
                    if (lastName != line.name)
                        viewCut.push({ type: 'speaker', text: line.name });
                    var dialogue = { type: 'dialogue-line', text: line.text };
                    if (line.voice){
                        dialogue.voice = "../files/voices/" + gt.settings.data.lang+ "/" + line.voice;
                    }
                    viewCut.push(dialogue);
                    lastName = line.name;
                }
                view.cutscenes.push(viewCut);
            }
        }

        // I see talk is not enabled by now, so just put it here ignored......anyway....
        if (lore.talk) {
            view.talk = [];
            for (var i = 0; i < lore.talk.length; i++) {
                var talk = lore.talk[i];
                var speakerNpc = gt.model.partial(gt.npc, talk.npcid);
                if (!speakerNpc)
                    continue;

                view.talk.push({ type: 'speaker', npc: speakerNpc, text: talk.name });
                for (var ii = 0; ii < talk.lines.length; ii++)
                    view.talk.push({ type: 'dialogue-line', text: talk.lines[ii] });
            }
        }

        return view;
    },

    getPartialViewModel: function(partial) {
        var view = {
            id: partial.i,
            type: 'quest',
            name: gt.model.name(partial),
            icon: gt.quest.getQuestIcon(partial.g, partial.r, partial.f),
            location: partial.l,
            sort: partial.s
        };

        var genre = gt.quest.genreIndex[partial.g];
        view.genre = genre.name || "Adventurer Quests";
        view.category = genre.category;
        view.section = genre.section;
        view.byline = view.location;
        return view;
    },

    getQuestIcon: function(genreId, repeatable, unlocksFunction) {
        // Special icons.
        if (unlocksFunction && repeatable)
            return '../files/icons/event/71342.png';

        if (repeatable)
            return '../files/icons/event/71222.png';

        if (unlocksFunction)
            return '../files/icons/event/71341.png';

        // Fall back to genre icons.
        return gt.quest.getGenreIcon(genreId);
    },

    getGenreIcon: function(genreId) {
        var genre = gt.quest.genreIndex[genreId];
        if (genre && genre.icon)
            return '../files/icons/journal/' + genre.icon + '.png';

        // Fall back to a regular quest icon if needed.
        return '../files/icons/journal/61411.png';
    }
};
