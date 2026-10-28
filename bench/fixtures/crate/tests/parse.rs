use semver_lite::{ParseError, Version};

#[test]
fn parses_plain_and_v_prefixed_versions() {
    assert_eq!("1.2.3".parse(), Ok(Version::new(1, 2, 3)));
    assert_eq!(" v10.0.42 ".parse(), Ok(Version::new(10, 0, 42)));
}

#[test]
fn rejects_malformed_versions() {
    assert_eq!("".parse::<Version>(), Err(ParseError::Empty));
    assert_eq!("1.2".parse::<Version>(), Err(ParseError::WrongPartCount(2)));
    assert_eq!(
        "1.x.3".parse::<Version>(),
        Err(ParseError::NotANumber("x".into()))
    );
    assert_eq!(
        "1.+2.3".parse::<Version>(),
        Err(ParseError::NotANumber("+2".into()))
    );
    assert_eq!(
        "01.2.3".parse::<Version>(),
        Err(ParseError::LeadingZero("01".into()))
    );
}

#[test]
fn displays_what_it_parses() {
    for text in ["0.0.0", "1.2.3", "20.10.7"] {
        assert_eq!(text.parse::<Version>().unwrap().to_string(), text);
    }
}
