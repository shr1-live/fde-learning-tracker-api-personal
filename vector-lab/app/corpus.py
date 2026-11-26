"""The approved document set (an invented company handbook) and the fixed question set for Week 2, Day 3.

Unlike the 4-document sample used on Days 1 and 2, this set has near-duplicate policies that differ by region and year
(annual leave in the UK and in Germany, an archived 2024 leave policy, hotel caps in Europe and the US). Without a metadata
filter those questions have no single right answer, which is the point of Day 3.
"""
from __future__ import annotations

from dataclasses import dataclass, field


@dataclass(frozen=True)
class Doc:
    doc_id: str
    title: str
    department: str
    region: str  # "All", "UK", "Germany", "US"
    year: int
    body: str


@dataclass(frozen=True)
class Q:
    question: str
    kind: str  # plain | paraphrase | filtered | out_of_scope
    docs: tuple[str, ...] = ()  # doc_ids that count as a correct source; empty = not in the documents
    phrase: str | None = None  # text a correct passage must contain
    filter: dict = field(default_factory=dict)  # metadata filter that a careful user would apply


DOCS: list[Doc] = [
    Doc("leave-uk-2025", "Leave policy (UK)", "hr", "UK", 2025,
        "Every full-time UK employee gets 24 days of paid annual leave per calendar year. Leave accrues monthly at 2 days per month. "
        "Up to 10 unused days can be carried over to the next year; anything above that expires on 31 March.\n\n"
        "Sick leave is separate from annual leave: 12 days per year. A medical certificate is needed when sick leave lasts more than 2 consecutive days. "
        "Tell your manager before 10:00 on the first day of absence.\n\n"
        "Parental leave is 26 weeks for the primary caregiver and 4 weeks for the other parent. Requests must be sent to HR at least 8 weeks before the expected start date."),
    Doc("leave-uk-2024", "Leave policy (UK, archived 2024)", "hr", "UK", 2024,
        "This policy is archived and was replaced in 2025. UK employees received 20 days of paid annual leave per calendar year. "
        "Up to 5 unused days could be carried over and the rest expired on 31 January.\n\n"
        "Sick leave was 10 days per year and a medical certificate was needed after 3 consecutive days of absence."),
    Doc("leave-de-2025", "Leave policy (Germany)", "hr", "Germany", 2025,
        "Every full-time employee in Germany gets 30 days of paid annual leave per calendar year, in line with the works agreement. "
        "Up to 5 unused days can be carried over; anything above that expires on 31 March.\n\n"
        "Sick leave is paid in full for up to 6 weeks. A medical certificate is needed from the fourth day of absence. Tell your manager before 09:00 on the first day.\n\n"
        "Public holidays follow the federal state of the office. Bridge days are announced by the people team in January."),
    Doc("expense-all-2025", "Expense policy", "finance", "All", 2025,
        "Business expenses are reimbursed within 14 days of a complete claim. Submit claims in the finance portal with the original receipt attached. Claims without receipts are rejected.\n\n"
        "Flights must be booked in economy class at least 14 days ahead. Hotel stays are capped at 120 euros per night in most cities and 180 euros in London. Meals during travel are covered up to 45 euros per day.\n\n"
        "Any single purchase above 500 euros needs written approval from a director before you buy. Software subscriptions are bought only by the IT team, never on a personal card."),
    Doc("travel-us-2025", "Travel policy (United States)", "finance", "US", 2025,
        "Employees travelling within the United States book flights in economy class at least 21 days ahead. Hotel stays are capped at 250 dollars per night in New York and San Francisco and 170 dollars elsewhere.\n\n"
        "Meals during travel are covered up to 75 dollars per day. Rental cars need approval from the team lead and must be the smallest suitable class. Claims are paid within 30 days."),
    Doc("remote-all-2025", "Remote work and security", "it", "All", 2025,
        "Employees may work remotely up to 3 days a week after completing their probation period of 3 months. Team days in the office are Tuesday and Thursday.\n\n"
        "Company laptops must use full disk encryption and lock automatically after 5 minutes. Use the VPN on any public Wi-Fi. Never store customer data on personal devices or in personal cloud drives.\n\n"
        "Report a lost device or a suspected phishing email to the security desk within 1 hour. Passwords must have at least 14 characters and multi-factor authentication is mandatory for all company accounts."),
    Doc("remote-de-2025", "Remote work addendum (Germany)", "it", "Germany", 2025,
        "Under the works agreement employees in Germany may work remotely up to 2 days a week. A home-office allowance of 40 euros per month is paid with the salary.\n\n"
        "Working time is recorded in the time-tracking tool and rest periods of 11 hours between working days are respected. Equipment such as a monitor can be ordered once through IT."),
    Doc("onboarding-all-2025", "Onboarding and training", "hr", "All", 2025,
        "New starters get a buddy for the first 8 weeks. The first week covers tools, security training and a tour of the main systems. A check-in with the manager happens at 30, 60 and 90 days.\n\n"
        "Each employee has a learning budget of 1,500 euros per year for courses, books and conferences. Conference attendance needs manager approval at least 30 days before the event. The learning budget does not roll over.\n\n"
        "Mentoring is open to everyone: ask the people team to match you with a mentor from another department. Mentoring pairs meet once every 2 weeks for 45 minutes."),
    Doc("incident-all-2025", "Security incident response", "it", "All", 2025,
        "A security incident is any lost device, leaked credential, suspected phishing email or unauthorised access. Report it to the security desk within 1 hour of noticing it, by phone or through the incident form.\n\n"
        "The security desk assigns a severity from 1 to 4 and a responder. Severity 1 incidents are reviewed by the leadership team within 24 hours. A written summary is shared within 5 working days."),
    Doc("procurement-all-2025", "Procurement and vendors", "finance", "All", 2025,
        "Vendor contracts above 5,000 euros need 3 written quotes and approval from the finance director. Contracts above 50,000 euros also need a security review and legal sign-off.\n\n"
        "Preferred vendors are listed in the finance portal. New vendors must complete the supplier questionnaire before the first purchase order is issued."),
    Doc("performance-all-2025", "Performance reviews", "hr", "All", 2025,
        "Performance reviews take place twice a year, in June and in December. Each review combines a self-assessment, manager feedback and a rating from 1 to 5.\n\n"
        "Pay changes are decided once a year after the December review. An employee who disagrees with a rating can ask for a second review by the next-level manager within 10 working days."),
    Doc("holidays-uk-2026", "Office holidays 2026 (UK)", "hr", "UK", 2026,
        "UK offices are closed on 1 January, 3 April, 6 April, 4 May, 25 May, 31 August, 25 December and 28 December 2026. The office closes at 14:00 on 24 December.\n\n"
        "Staff who work on a bank holiday receive a day off in lieu within 3 months."),
]

QUESTIONS: list[Q] = [
    # Plain: the question uses the words of one document and has one right source.
    Q("How soon are expense claims reimbursed?", "plain", ("expense-all-2025",), "within 14 days"),
    Q("After how many minutes does a company laptop lock itself?", "plain", ("remote-all-2025",), "5 minutes"),
    Q("How long must a password be?", "plain", ("remote-all-2025",), "14 characters"),
    Q("What is the yearly learning budget?", "plain", ("onboarding-all-2025",), "1,500 euros"),
    Q("How often do mentoring pairs meet?", "plain", ("onboarding-all-2025",), "once every 2 weeks"),
    Q("How many written quotes does a vendor contract above 5,000 euros need?", "plain", ("procurement-all-2025",), "3 written quotes"),
    Q("How often are performance reviews held?", "plain", ("performance-all-2025",), "twice a year"),
    Q("How long does a new starter have a buddy?", "plain", ("onboarding-all-2025",), "8 weeks"),
    Q("Within how long must a phishing email be reported?", "plain", ("remote-all-2025", "incident-all-2025"), "within 1 hour"),
    Q("How is the severity of a security incident rated?", "plain", ("incident-all-2025",), "severity from 1 to 4"),
    # Paraphrase: other words for the same fact (keyword search is expected to struggle).
    Q("Can I work from home?", "paraphrase", ("remote-all-2025", "remote-de-2025"), "remotely"),
    Q("Who signs off on a purchase above 500 euros?", "paraphrase", ("expense-all-2025",), "director"),
    Q("What do I need to do before buying something expensive?", "paraphrase", ("expense-all-2025", "procurement-all-2025"), "approval"),
    Q("When can I appeal my performance score?", "paraphrase", ("performance-all-2025",), "second review"),
    Q("What does the company pay for my courses and conferences?", "paraphrase", ("onboarding-all-2025",), "learning budget"),
    Q("Which days are people expected to be in the office?", "paraphrase", ("remote-all-2025",), "Tuesday and Thursday"),
    # Filtered: near-duplicate documents; the filter picks the right one. Run with and without it.
    Q("How many days of paid annual leave do I get?", "filtered", ("leave-uk-2025",), "24 days", {"$and": [{"region": "UK"}, {"year": {"$gte": 2025}}]}),
    Q("How many days of paid annual leave do I get?", "filtered", ("leave-de-2025",), "30 days", {"region": "Germany"}),
    Q("What is the hotel cap per night?", "filtered", ("travel-us-2025",), "250 dollars", {"region": "US"}),
    Q("How many days a week can I work remotely?", "filtered", ("remote-de-2025",), "2 days a week", {"region": "Germany"}),
    Q("How many unused leave days can be carried over?", "filtered", ("leave-uk-2025",), "10 unused days", {"$and": [{"region": "UK"}, {"year": {"$gte": 2025}}]}),
    Q("When does a medical certificate become necessary?", "filtered", ("leave-de-2025",), "fourth day", {"region": "Germany"}),
    # Not in the documents: the assistant must say there is not enough evidence.
    Q("What is the company dress code?", "out_of_scope"),
    Q("How much is the annual bonus?", "out_of_scope"),
    Q("When do stock options vest?", "out_of_scope"),
]
