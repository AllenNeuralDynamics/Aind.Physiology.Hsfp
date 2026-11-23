import abc
from typing import Generic, TypeVar

TMapTo = TypeVar("TMapTo")


class DataMapper(abc.ABC, Generic[TMapTo]):
    """
    Abstract base class for data mappers.

    Defines the interface for mapping data from various sources to specific
    target formats or schemas.
    """

    _mapped: TMapTo | None

    @abc.abstractmethod
    def map(self) -> TMapTo:
        """
        Maps data to the target schema or format.

        Returns:
            The mapped data object
        """

    def is_mapped(self) -> bool:
        """
        Checks if the data has been successfully mapped.

        Returns:
            True if the data is mapped, False otherwise
        """
        return self._mapped is not None

    @property
    def mapped(self) -> TMapTo:
        """
        Retrieves the mapped data object.

        Returns:
            The mapped data object

        Raises:
            ValueError: If the data has not been mapped yet
        """
        if not self.is_mapped():
            raise ValueError("Data not yet mapped")
        assert self._mapped is not None, "Mapped data should not be None"
        return self._mapped
