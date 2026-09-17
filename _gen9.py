# -*- coding: utf-8 -*-
import os
os.chdir(r"D:\SteamLibrary\steamapps\common\RimWorld\Mods\Aooni")

# ============ 1) New "Ao Oni basics" research ============
p = "Defs/Misc/AoOniMisc.xml"
s = open(p, encoding="utf-8").read()

old = '''  <ResearchProjectDef>
    <defName>AoOniInfectionTest</defName>'''
new = '''  <ResearchProjectDef>
    <defName>AoOniBasics</defName>
    <label>onionomy</label>
    <description>A first survey of the Ao Oni. Studying this unlocks the entity codex page for the Ao Oni - only variants you have personally encountered will be catalogued.</description>
    <tab>Anomaly</tab>
    <knowledgeCost>5</knowledgeCost>
    <knowledgeCategory>Basic</knowledgeCategory>
    <researchViewX>3.20</researchViewX>
    <researchViewY>6.40</researchViewY>
  </ResearchProjectDef>

  <ResearchProjectDef>
    <defName>AoOniInfectionTest</defName>'''
assert old in s, "basics anchor"
s = s.replace(old, new)

# infection test: now requires basics
s = s.replace('''  <RecipeDef>
    <defName>AoOniInfectionTest</defName>''','''  <RecipeDef>
    <defName>AoOniInfectionTest</defName>''')
old = '''    <researchPrerequisite>
      <li>AoOniInfectionTest</li>
    </researchPrerequisite>
    <recipeUsers>'''
new = '''    <researchPrerequisite>
      <li>AoOniBasics</li>
    </researchPrerequisite>
    <recipeUsers>'''
assert old in s, "test recipe research"
s = s.replace(old, new)

# careful fishing: move after basics (prerequisite basics, earlier view pos)
old = '''    <prerequisites>
      <li>AoOniInfectionTest</li>
    </prerequisites>
    <knowledgeCost>10</knowledgeCost>
    <knowledgeCategory>Basic</knowledgeCategory>
    <researchViewX>5.00</researchViewX>
    <researchViewY>5.60</researchViewY>'''
new = '''    <prerequisites>
      <li>AoOniBasics</li>
    </prerequisites>
    <knowledgeCost>10</knowledgeCost>
    <knowledgeCategory>Basic</knowledgeCategory>
    <researchViewX>4.20</researchViewX>
    <researchViewY>6.40</researchViewY>'''
assert old in s, "fishing prereq"
s = s.replace(old, new)
open(p, "w", encoding="utf-8").write(s)
print("research xml ok")

# ============ 2) Other research defs: gate infection-test behind basics ============
p = "Defs/Misc/AoOniFeatures.xml"
s = open(p, encoding="utf-8").read()
old = '''    <prerequisites>
      <li>AoOniInfectionTest</li>
    </prerequisites>
    <knowledgeCost>10</knowledgeCost>
    <knowledgeCategory>Basic</knowledgeCategory>
    <researchViewX>5.00</researchViewX>
    <researchViewY>5.60</researchViewY>'''
new = '''    <prerequisites>
      <li>AoOniBasics</li>
    </prerequisites>
    <knowledgeCost>10</knowledgeCost>
    <knowledgeCategory>Basic</knowledgeCategory>
    <researchViewX>4.20</researchViewX>
    <researchViewY>6.40</researchViewY>'''
assert old in s, "features fishing prereq"
s = s.replace(old, new)
open(p, "w", encoding="utf-8").write(s)
print("features xml ok")

# ============ 3) Codex: gate behind research; hide individual variants ============
p = "Defs/Misc/AoOniMisc.xml"
s = open(p, encoding="utf-8").read()
old = '''    <discoveredResearchProjects>
      <li>AoOniInfectionTest</li>
    </discoveredResearchProjects>'''
new = '''    <discoveredResearchProjects>
      <li>AoOniBasics</li>
    </discoveredResearchProjects>'''
assert old in s, "codex research link"
s = s.replace(old, new)
open(p, "w", encoding="utf-8").write(s)
print("codex link ok")

# race defs: hide codex/inspect until discovered is handled by codex "linkedThings";
# also hide variants from codex by only listing the base oni (already the case).
print("all xml ok")
