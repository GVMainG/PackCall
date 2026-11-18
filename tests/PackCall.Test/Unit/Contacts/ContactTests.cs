using PackCall.Core.Entities;
using PackCall.Core.Exceptions;

namespace PackCall.Core.Test;

[Trait("Category", "Unit")]
[Trait("Module", "Contacts")]
public class ContactTests
{
    [Theory]
    [InlineData(" +00042 ", "42")]
    [InlineData("-00042", "-42")]
    [InlineData("-9223372036854775808", "-9223372036854775808")]
    [InlineData("9223372036854775807", "9223372036854775807")]
    public void CreateNormalizesChannelAddresses(string chatId, string expected)
    {
        var contact = Contact.Create([("email", " User@Example.COM "), ("telegram", chatId)], DateTime.UnixEpoch);
        Assert.Equal("user@example.com", contact.Email);
        Assert.Equal(expected, Assert.Single(contact.Channels, c => c.Channel == "telegram").Address);
    }

    [Theory]
    [InlineData("telegram", "0")]
    [InlineData("telegram", "1.5")]
    [InlineData("telegram", "9223372036854775808")]
    [InlineData("telegram", "-9223372036854775809")]
    [InlineData("email", "Name <user@example.com>")]
    [InlineData("email", null)]
    [InlineData("sms", "42")]
    public void CreateRejectsInvalidAddress(string channel, string? address) =>
        Assert.Throws<ContactValidationException>(() => Contact.Create([(channel, address)], DateTime.UnixEpoch));

    [Fact]
    public void CreateRejectsNullTerminatedChatId() =>
        Assert.Throws<ContactValidationException>(() => Contact.Create([("telegram", "42\0")], DateTime.UnixEpoch));

    [Fact]
    public void CreateEnforcesEmailLengthLimit()
    {
        var address = $"user@{new string('a', 60)}.{new string('b', 62)}.{new string('c', 62)}.{new string('d', 63)}";
        Assert.Equal(255, address.Length);
        Assert.Equal(address, Contact.Create([("email", address)], DateTime.UnixEpoch).Email);
        Assert.Throws<ContactValidationException>(() => Contact.Create([("email", "x" + address)], DateTime.UnixEpoch));
    }

    [Fact]
    public void RejectsEmptyAndDuplicateChannelsWithoutChangingContact()
    {
        var contact = Contact.Create([("email", "original@example.com")], DateTime.UnixEpoch);
        var later = DateTime.UnixEpoch.AddSeconds(1);
        Assert.Throws<ContactValidationException>(() => Contact.Create([], later));
        Assert.Throws<ContactValidationException>(() => contact.ReplaceAddresses([], later));
        Assert.Throws<ContactValidationException>(() => contact.ReplaceAddresses(
            [("email", "new@example.com"), ("telegram", "0")], later));
        Assert.Throws<ContactValidationException>(() => contact.ReplaceAddresses(
            [("email", "new@example.com"), ("email", "second@example.com")], later));
        Assert.Equal("original@example.com", Assert.Single(contact.Channels).Address);
        Assert.Equal(DateTime.UnixEpoch, contact.UpdatedAt);
    }
}
