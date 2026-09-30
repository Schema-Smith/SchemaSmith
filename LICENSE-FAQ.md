# SchemaSmith License FAQ

SchemaSmith Community is free to use — including for commercial and internal use at any scale — under the SchemaSmith Community License (SSCL) v2.0. This page answers the questions security, procurement, and legal teams most often ask when evaluating it.

It is a plain-language summary provided for convenience, not legal advice. The [SSCL v2.0 license text](LICENSE) is authoritative and governs wherever the two differ. Section references below (§) point into that text.

## Is SchemaSmith free?

Yes. The SchemaSmith command-line tools are free to use for any purpose — personal, commercial, internal, and educational — with no restriction on organization size, revenue, database size, or number of environments (§1a). There is no per-seat, per-server, or per-environment fee.

## Is SchemaSmith open source?

SchemaSmith is **source-available**, not open source. The full source is public, and you may read it, build it, and modify it for your own use (§1b). SSCL v2.0 is not an OSI-approved open-source license, because it restricts redistribution and offering the software as a service (§3). Software-composition-analysis (SCA) and SBOM tools that classify licenses by OSI status will report it as a non-standard, source-available license — see the next question for how to record it.

## What license identifier should we record in our SBOM or SCA tool?

Use the SPDX custom-license reference **`LicenseRef-SSCL-2.0`**. SSCL v2.0 is not on the SPDX License List, so a `LicenseRef` is the correct way to name it rather than leaving it classified as "unknown." The repository declares it machine-readably: the license text is at [`LICENSES/LicenseRef-SSCL-2.0.txt`](LICENSES/LicenseRef-SSCL-2.0.txt), and [`REUSE.toml`](REUSE.toml) assigns it to the SchemaSmith-authored source by path.

GitHub's license detection reports this repository's license as "Other" (`NOASSERTION`). That is expected for any license that isn't on the SPDX List, and it does not affect the identifier above.

## Can we use SchemaSmith commercially and internally at any scale?

Yes — without restriction on organization size, revenue, database size, or number of environments (§1a). Internal commercial use across any number of teams, servers, and environments is permitted.

## Can we manage databases for our own products and services — including databases our customers host?

Yes. Using SchemaSmith to manage databases for your own products or services, whether you or your customers host those databases, is explicitly **not** redistribution (§3a). The license states this twice, the second time in a closing clarification paragraph.

## Can we include SchemaSmith in our own product's installer or update process?

Yes. Including SchemaSmith's deployment components in your product's installation or update process, solely to apply the database changes your own product requires, is explicitly permitted (§3a).

## Can a consultant or systems integrator set it up for us?

Yes. Setup, configuration, and implementation services that let you — the licensed user — operate SchemaSmith for your own use are permitted (§3a). The boundary is ongoing work on your behalf: a third party operating SchemaSmith as a perpetual database management, maintenance, or migration service, with SchemaSmith as the primary tool, is treated as offering the software as a service and is not permitted (§3a). A partner may help you stand it up; your own organization operates it.

## What is not permitted?

The license lists four restrictions (§3). You may not:

- redistribute SchemaSmith, in source or binary form, as a standalone product, as a component marketed or provided to third parties within another product, or as a service, whether free or paid;
- offer it as a hosted or managed service (SaaS, PaaS, or similar) where third parties interact with SchemaSmith functionality;
- remove, alter, or obscure copyright notices, license text, or attribution;
- use the SchemaSmith name, logo, or branding to imply endorsement of your product or service.

Ordinary internal use and use for your own products, described above, are not affected.

## Is there a patent grant?

Yes. Each contributor grants you a perpetual, worldwide, royalty-free, non-exclusive patent license to make, use, and run their contributions (§2).

## The license can change in future versions. Does that change the version we already use?

No. Each version of SchemaSmith is governed by the license distributed with that version (§8). A later version may be released under different terms; those terms govern that later version, not the one you already have. Continuing to use the version you adopted keeps you on its terms.

## Is there a warranty?

No. SchemaSmith is provided "as is," without warranty of any kind, and the authors and copyright holders are not liable for claims or damages arising from its use (§5). This is standard for freely licensed software.

## What law governs the license?

The laws of the State of Florida, USA, without regard to conflict-of-laws provisions, with exclusive jurisdiction in the state and federal courts located in Florida (§7).

## What happens if we breach a term?

The license terminates automatically on breach, and you must stop using the software (§6). If it is your first breach and you cure it within 30 days of becoming aware of it, the license is reinstated automatically. After a first breach, reinstatement requires written permission from the SchemaSmith project maintainers. Licenses you granted for your own contributions survive termination (§4, §6).

## What do we agree to if we contribute?

A contribution is licensed under the same SSCL v2.0 terms. You confirm you have the right to make it and, to the best of your knowledge, that it doesn't violate anyone else's intellectual property. You grant the SchemaSmith maintainers a perpetual, worldwide, royalty-free, non-exclusive license to use, modify, and distribute it in SchemaSmith's database management products. You keep ownership of your contribution (§4).

## Who do we contact with a licensing question?

Email **support@schemasmith.com**.
